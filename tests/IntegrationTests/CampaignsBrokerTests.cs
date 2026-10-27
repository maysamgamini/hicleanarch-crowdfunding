using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;

using BuildingBlocks.Infrastructure.Events;

using Campaigns.Contracts;

using CrowdFunding.Messaging;
using CrowdFunding.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Pledges.Contracts;
using Pledges.Infrastructure;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l07: the events between Campaigns and the other modules cross SNS and SQS (Moto) while
/// Campaigns still runs in the monolith; Campaigns' own events stay in process; the publish gap has an
/// honest answer and a bounded retry; switching the routes back loses nothing and handles nothing twice.
/// </summary>
public sealed class CampaignsBrokerTests(Database database) : IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    /// <summary>A monolith (with its Payments, Notifications and Campaigns services) on its own topics and queues, or on
    /// <paramref name="broker"/>'s. Since s5-l09 Campaigns runs only in its own service, so s5-l07's rollback is gone.</summary>
    private WebApplicationFactory<Program> Host(MotoBroker.Provisioned? broker)
    {
        var host = database.StartHost((broker ?? database.Provision()).Settings)
            .WithWebHostBuilder(web => web.UseSetting("Pledges:FundingCancel:Open", "true"));
        _hosts.Add(host);
        _ = host.Server; // started
        return host;
    }

    private static Guid CampaignIdOf(string publicId) => Guid.ParseExact(publicId["cmp_".Length..], "N");

    private async Task<long> Scalar(string sql, object value)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<long> Windows(Guid campaignId) => Scalar("SELECT count(*) FROM pledges.funding_windows WHERE campaign_id = @value", campaignId);

    private Task<long> Inbox(string schema, string handler, Guid eventId) =>
        Scalar($"SELECT count(*) FROM {schema}.inbox WHERE handler = '{handler}' AND event_id = @value", eventId);

    private Task<Guid> PublishedEventOf(Guid campaignId) => Single<Guid>(
        "SELECT id FROM campaigns.outbox WHERE type = 'CampaignPublished' AND payload->>'campaignId' = @value", campaignId.ToString());

    private async Task<T> Single<T>(string sql, object value)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// Holds CampaignPublished back from the monolith's queue, as a slow or failed delivery would: the
    /// subscription's filter leaves it out until <see cref="Release"/>. The other services' copies still go.
    /// </summary>
    private static async Task<string> HoldBack(MotoBroker broker, MotoBroker.Provisioned provisioned)
    {
        using var sns = broker.Sns();
        var queueArn = provisioned.Settings["Events:Broker:Queues:monolith:Arn"];
        var subscriptions = await sns.ListSubscriptionsByTopicAsync(provisioned.Topics["Campaigns"], Ct);
        var subscription = subscriptions.Subscriptions.Single(s => s.Endpoint == queueArn).SubscriptionArn;
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest(
            subscription, "FilterPolicy", "{\"event_type\":[\"CampaignCancelled\",\"CampaignCreatorRecorded\"]}"), Ct);
        return subscription;
    }

    /// <summary>The plan's filter again, and the held event published once more, as SNS's delayed delivery would bring it.</summary>
    private async Task Release(MotoBroker broker, string subscription, WebApplicationFactory<Program> host, Guid campaignId)
    {
        using (var sns = broker.Sns())
        {
            var filter = BrokerPlan.Load(Path.Combine(AppContext.BaseDirectory, "events", "subscriptions.json"))
                .Subscriptions.Single(s => s.Topic == "Campaigns" && s.Queue == "monolith").FilterPolicy;
            await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest(subscription, "FilterPolicy", filter), Ct);
        }

        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var read = new NpgsqlCommand(
            "SELECT id, payload::text, trace_parent FROM campaigns.outbox WHERE type = 'CampaignPublished' AND payload->>'campaignId' = @value", connection);
        read.Parameters.AddWithValue("value", campaignId.ToString());
        await using var row = await read.ExecuteReaderAsync(Ct);
        Assert.True(await row.ReadAsync(Ct));
        var record = new OutboxRecord("Campaigns", row.GetGuid(0), nameof(CampaignPublished), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2));
        Assert.Single(await host.Services.GetRequiredService<IEventPublisher>().Publish([record], Ct));
    }

    private static Func<HttpRequestMessage> PledgeRequest(string campaignId) =>
        () => new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaignId}/pledges") { Content = JsonContent.Create(Scenario.Pledge(5_000)) };

    private static async Task AssertNotFoundWithTheHint(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("campaign_not_found", problem.GetProperty("reason").GetString());
        Assert.Equal(PublishGapHint.Detail, problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_pledge_sent_while_CampaignPublished_is_held_back_gets_404_with_the_hint_and_the_clients_retry_succeeds_once_it_arrives()
    {
        var provisioned = database.Provision();
        var host = Host(provisioned);
        var subscription = await HoldBack(database.Broker, provisioned);
        var given = new Scenario(database, host);
        var id = await given.Draft();
        (await given.Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct)).EnsureSuccessStatusCode();
        var campaignId = CampaignIdOf(id);
        var backer = given.Backer(Guid.NewGuid());

        // The gap: the catalog says LIVE, Pledges has no window, and both of its paths say so with the hint.
        await AssertNotFoundWithTheHint(await backer.SendAsync(PledgeRequest(id)(), Ct));
        await AssertNotFoundWithTheHint(await given.Creator().PostAsync($"/v1/campaigns/{id}/funding/cancel", null, Ct));
        Assert.Equal(0, await Windows(campaignId));

        // The client retries; the event arrives during its first wait.
        var pledging = Scenario.SendThroughPublishGap(backer, id, PledgeRequest(id));
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        await Release(database.Broker, subscription, host, campaignId);
        var (response, sent) = await pledging;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.InRange(sent, 2, 1 + Scenario.PublishGapWaits.Count);
        Assert.Equal(1, await Windows(campaignId));
        Assert.Equal(1, await Scalar("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @value", campaignId));
    }

    [Fact]
    public async Task A_pledge_for_a_draft_or_an_unknown_id_gets_404_and_is_not_retried()
    {
        var host = Host(database.Provision());
        var given = new Scenario(database, host);
        var draft = await given.Draft();
        var backer = given.Backer(Guid.NewGuid());

        foreach (var id in new[] { draft, $"cmp_{Guid.CreateVersion7():N}" })
        {
            var started = DateTime.UtcNow;
            var (response, sent) = await Scenario.SendThroughPublishGap(backer, id, PledgeRequest(id));

            await AssertNotFoundWithTheHint(response);
            Assert.Equal(1, sent);
            Assert.True(DateTime.UtcNow - started < Scenario.PublishGapWaits[0], "the client waited before giving up");
        }
    }

    [Fact]
    public async Task Campaigns_own_catalog_invalidation_still_runs_in_process_while_the_other_modules_copies_wait_on_the_broker()
    {
        var provisioned = database.Provision();
        var host = Host(provisioned);
        await HoldBack(database.Broker, provisioned);
        var given = new Scenario(database, host);
        var id = await given.Draft();
        (await given.Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct)).EnsureSuccessStatusCode();
        var eventId = await PublishedEventOf(CampaignIdOf(id));

        Assert.Equal("Campaigns,broker", await Single<string>("SELECT array_to_string(sinks, ',') FROM campaigns.outbox WHERE id = @value", eventId));
        await Scenario.Eventually(async () => await Inbox("campaigns", "Campaigns.CatalogCacheInvalidation", eventId) == 1);
        await Scenario.Eventually(async () => await Scalar("SELECT count(*) FROM campaigns.outbox WHERE id = @value AND published_at IS NOT NULL", eventId) == 1);
        Assert.Equal(0, await Windows(CampaignIdOf(id))); // Pledges' copy is the one held back
    }

    /// <summary>A message as SNS delivers it with raw delivery: the event's body and its attributes.</summary>
    private static SendMessageRequest Message<T>(string queueUrl, T integrationEvent, string producer) => new()
    {
        QueueUrl = queueUrl,
        MessageBody = JsonSerializer.Serialize(integrationEvent, JsonSerializerOptions.Web),
        MessageAttributes = new()
        {
            [MessageAttributes.EventType] = new() { DataType = "String", StringValue = typeof(T).Name },
            [MessageAttributes.EventVersion] = new() { DataType = "String", StringValue = "1" },
            [MessageAttributes.Producer] = new() { DataType = "String", StringValue = producer },
        },
    };
}
