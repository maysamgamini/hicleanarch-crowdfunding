using System.Net.Http.Json;
using System.Text.Json;

using Amazon.SQS;
using Amazon.SQS.Model;

using BuildingBlocks.Application;

using Campaigns.Contracts;

using CrowdFunding.Messaging;

using Microsoft.AspNetCore.Mvc.Testing;

using Npgsql;

using Payments.Contracts;

using Pledges.Contracts;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l02: every event Notifications consumes comes through its own queue (SNS and SQS on Moto).
/// Its emails do not depend on the order the events arrive in. From s5-l04 only the Notifications service
/// beside the monolith reads that queue; the route-back and the old-queue tests went with the switch.
/// </summary>
public sealed class NotificationsBrokerTests(Database database) : IDisposable
{
    private const string Handler = "Notifications.Handlers";

    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private WebApplicationFactory<Program> Host(IReadOnlyDictionary<string, string?> broker)
    {
        var host = database.StartHost(broker);
        _hosts.Add(host);
        _ = host.Services; // started
        return host;
    }

    private static int SentTo(Guid user, string subject) => Database.Mail.Sent.Count(m => m.RecipientId == user && m.Subject == subject);

    /// <summary>The event as SNS delivers it to a queue with raw delivery: its body and its attributes.</summary>
    private static async Task Send<TEvent>(IAmazonSQS sqs, string queueUrl, TEvent e, string producer)
        where TEvent : IntegrationEvent =>
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(e, JsonSerializerOptions.Web),
            MessageAttributes = new()
            {
                [MessageAttributes.EventType] = new() { DataType = "String", StringValue = typeof(TEvent).Name },
                [MessageAttributes.EventVersion] = new() { DataType = "String", StringValue = "1" },
                [MessageAttributes.Producer] = new() { DataType = "String", StringValue = producer },
            },
        }, Ct);

    private async Task<long> Scalar(string sql, object value)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<long> Handled(Guid eventId) =>
        Scalar($"SELECT count(*) FROM notifications.inbox WHERE handler = '{Handler}' AND event_id = @value", eventId);

    /// <summary>Sends one event and waits until Notifications has handled it, so the next one arrives after it.</summary>
    private async Task SendAndWait<TEvent>(IAmazonSQS sqs, string queueUrl, TEvent e, string producer)
        where TEvent : IntegrationEvent
    {
        await Send(sqs, queueUrl, e, producer);
        await Scenario.Eventually(async () => await Handled(e.EventId) == 1);
    }

    private static async Task Empty(IAmazonSQS sqs, string queueUrl) =>
        await Scenario.Eventually(async () =>
            (await sqs.GetQueueAttributesAsync(queueUrl, ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"], Ct))
                .Attributes.Values.All(v => v == "0"));

    private async Task<bool> OnTheList(Guid campaignId, Guid backerId) => await Scalar($"""
        SELECT count(*) FROM notifications.campaign_pledges
        WHERE campaign_id = @value AND backer_id = '{backerId}'
          AND activated_at IS NOT NULL AND cancelled_at IS NULL AND released_at IS NULL
        """, campaignId) > 0;

    private static PledgeActivated Activated(Guid pledgeId, Guid campaignId, Guid backerId) =>
        new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, pledgeId, campaignId, backerId, null, 6_000, "USD");

    private static PledgeCancelled Cancelled(Guid pledgeId, Guid campaignId, Guid backerId) =>
        new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, pledgeId, campaignId, backerId, 6_000);

    [Fact]
    public async Task A_pledge_placed_through_the_api_reaches_Notifications_through_its_queue_and_its_row_owes_the_broker()
    {
        var host = Host(database.Provision().Settings);
        var given = new Scenario(database, host);
        var campaign = await given.Publish();
        var backer = Guid.NewGuid();

        (await given.Backer(backer).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();

        await Scenario.Eventually(() => Task.FromResult(SentTo(backer, "You're a backer") == 1));
        // Written under Broker: Pledges' own consumer in process, the broker for Notifications; no in-process sink for it.
        Assert.Equal(1, await Scalar("""
            SELECT count(*) FROM pledges.outbox
            WHERE type = 'PledgeActivated' AND payload->>'backerId' = @value
              AND sinks = ARRAY['Pledges', 'broker'] AND published_at IS NOT NULL
            """, backer.ToString()));
    }

    [Fact]
    public async Task An_event_delivered_twice_sends_one_email()
    {
        var provisioned = database.Provision();
        Host(provisioned.Settings);
        using var sqs = database.Broker.Sqs();
        var queue = provisioned.Settings["Events:Broker:Queues:notifications:Url"]!;
        var backer = Guid.NewGuid();
        var activated = Activated(Guid.NewGuid(), Guid.NewGuid(), backer);

        await Send(sqs, queue, activated, "Pledges");
        await Send(sqs, queue, activated, "Pledges"); // SNS delivers at least once

        await Scenario.Eventually(() => Task.FromResult(SentTo(backer, "You're a backer") == 1));
        await Empty(sqs, queue);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct); // a second email, if one were queued, would be sent by now
        Assert.Equal(1, SentTo(backer, "You're a backer"));
        Assert.Equal(1, await Handled(activated.EventId));
    }

    /// <summary>A backer cancels P1 and pledges again with P2; the cancel and the new activation arrive through the queue either way round.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PledgeCancelled_of_one_pledge_and_PledgeActivated_of_the_next_keep_the_backer_whichever_arrives_first(bool cancelFirst)
    {
        var provisioned = database.Provision();
        Host(provisioned.Settings);
        using var sqs = database.Broker.Sqs();
        var queue = provisioned.Settings["Events:Broker:Queues:notifications:Url"]!;
        var (campaign, backer, first, second) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SendAndWait(sqs, queue, Activated(first, campaign, backer), "Pledges");

        if (cancelFirst)
        {
            await SendAndWait(sqs, queue, Cancelled(first, campaign, backer), "Pledges");
            await SendAndWait(sqs, queue, Activated(second, campaign, backer), "Pledges");
        }
        else
        {
            await SendAndWait(sqs, queue, Activated(second, campaign, backer), "Pledges");
            await SendAndWait(sqs, queue, Cancelled(first, campaign, backer), "Pledges");
        }

        Assert.True(await OnTheList(campaign, backer));
    }

    [Theory]
    [InlineData(CampaignFundingDecided.Funded, "The campaign is funded")]
    [InlineData(CampaignFundingDecided.Unsuccessful, "The campaign was not funded")]
    public async Task A_PledgeActivated_delivered_after_the_decision_leaves_the_backer_told_once_by_their_PledgeDecided(string outcome, string subject)
    {
        var provisioned = database.Provision();
        Host(provisioned.Settings);
        using var sqs = database.Broker.Sqs();
        var queue = provisioned.Settings["Events:Broker:Queues:notifications:Url"]!;
        var (campaign, creator, backer, pledge) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SendAndWait(sqs, queue, new CampaignPublished(Guid.CreateVersion7(), DateTimeOffset.UtcNow, campaign, creator, 5_000, "USD",
            DateTimeOffset.UtcNow.AddDays(30), []), "Campaigns");

        // The decision first: the campaign's for the creator, the pledge's for its backer; the pledge's activation comes late.
        await SendAndWait(sqs, queue, new CampaignFundingDecided(Guid.CreateVersion7(), DateTimeOffset.UtcNow, campaign, outcome,
            outcome == CampaignFundingDecided.Funded ? 6_000 : 1_000, 1, 5_000), "Pledges");
        await SendAndWait(sqs, queue, new PledgeDecided(Guid.CreateVersion7(), DateTimeOffset.UtcNow, pledge, campaign, backer, outcome, 6_000, "USD"), "Pledges");
        await SendAndWait(sqs, queue, Activated(pledge, campaign, backer), "Pledges");

        await Scenario.Eventually(() => Task.FromResult(SentTo(backer, subject) == 1 && SentTo(backer, "You're a backer") == 1));
        await Scenario.Eventually(() => Task.FromResult(Database.Mail.Sent.Count(m => m.RecipientId == creator) == 1));
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(1, SentTo(backer, subject));
        Assert.Equal(2, Database.Mail.Sent.Count(m => m.RecipientId == backer)); // the decision and the confirmation, nothing else
    }
}
