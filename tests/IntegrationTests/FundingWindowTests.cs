using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Events;

using Campaigns.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Pledges hears Campaigns: publishing opens a funding window, once. A LIVE campaign's cancel is Pledges' own
/// (s5-l05; since s5-l08 the only path): Pledges answers it, and the catalog follows its FundingCancelled.
/// </summary>
public sealed class FundingWindowTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Publishing_a_campaign_opens_its_funding_window_with_its_tiers()
    {
        var given = new Scenario(database, _host);

        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 500), ("Standard copy", 5_900, null));

        Assert.Equal("LIVE", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(5_000_000L, await given.Scalar<long>("SELECT goal_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(2L, await given.Scalar<long>("SELECT count(*) FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.tier_counters WHERE campaign_id = @id AND limit_qty IS NULL", campaign.CampaignId));
    }

    [Fact]
    public async Task A_redelivered_CampaignPublished_changes_nothing_the_second_time()
    {
        // Pledges gets CampaignPublished from the Campaigns service through its queue (s5-l09); SQS delivers at
        // least once, so the message is sent again as SQS would.
        var provisioned = database.Provision();
        await using var host = database.StartHost(provisioned.Settings);
        var given = new Scenario(database, host);
        var campaign = await given.Publish();
        // Something Pledges did since, which a second open must not undo.
        await given.Execute("UPDATE pledges.funding_windows SET goal_minor = 777 WHERE campaign_id = @id", campaign.CampaignId);

        var eventId = await given.Scalar<Guid>(
            "SELECT id FROM campaigns.outbox WHERE type = 'CampaignPublished' AND payload->>'campaignId' = @id::text", campaign.CampaignId);
        var payload = await given.Scalar<string>("SELECT payload::text FROM campaigns.outbox WHERE id = @id", eventId);
        using var sqs = database.Broker.Sqs();
        await sqs.SendMessageAsync(new Amazon.SQS.Model.SendMessageRequest
        {
            QueueUrl = provisioned.Settings["Events:Broker:Queues:pledges:Url"]!,
            MessageBody = payload,
            MessageAttributes = new()
            {
                [CrowdFunding.Messaging.MessageAttributes.EventType] = new() { DataType = "String", StringValue = nameof(CampaignPublished) },
                [CrowdFunding.Messaging.MessageAttributes.EventVersion] = new() { DataType = "String", StringValue = "1" },
                [CrowdFunding.Messaging.MessageAttributes.Producer] = new() { DataType = "String", StringValue = CampaignsModuleName.Value },
            },
        }, Ct);
        await Scenario.Eventually(async () => await given.Scalar<long>(
            "SELECT count(*) FROM pledges.inbox WHERE event_id = @id", eventId) == 1);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // the copy has met the inbox by now

        Assert.Equal(777L, await given.Scalar<long>("SELECT goal_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.inbox WHERE event_id = @id", eventId));
    }

    [Fact]
    public async Task A_live_campaign_is_cancelled_when_Pledges_accepts()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();

        var cancelled = await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/funding/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("CANCELLED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        await Scenario.Eventually(async () =>
            (await Database.Client(_host).GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct)).GetProperty("status").GetString() == "CANCELLED");
    }

    /// <summary>Only the creator may cancel: Pledges refuses another creator, so funding stays open.</summary>
    [Fact]
    public async Task Another_creators_cancel_is_403_and_the_funding_window_stays_live()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var someoneElse = Database.Client(_host, Database.Token(BuildingBlocks.Infrastructure.Identity.Roles.Creator));

        var refused = await someoneElse.PostAsync($"/v1/campaigns/{campaign.Id}/funding/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("LIVE", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM pledges.outbox WHERE type = 'FundingCancelled' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
    }

    /// <summary>
    /// Pledges accepted the cancel, but the request failed before Campaigns recorded it. Pledges'
    /// FundingCancelled event reaches Campaigns anyway, and the catalog follows.
    /// </summary>
    [Fact]
    public async Task An_accepted_cancel_reaches_the_catalog_even_if_the_request_failed_after()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<BuildingBlocks.Application.IDispatcher>();
            var accepted = await dispatcher.Send<Pledges.Contracts.CancelFunding, Pledges.Contracts.CancelFundingAccepted>(
                new Pledges.Contracts.CancelFunding(campaign.CampaignId), Ct);
            Assert.True(accepted.IsSuccess);
        }

        await Scenario.Eventually(async () =>
            (await Database.Client(_host).GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct)).GetProperty("status").GetString() == "CANCELLED");
    }

    [Fact]
    public async Task A_cancel_after_the_campaign_has_ended_is_refused_and_changes_nothing()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET status = 'ENDED' WHERE campaign_id = @id", campaign.CampaignId);

        var refused = await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/funding/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("campaign_closed", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        var read = await Database.Client(_host).GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct);
        Assert.Equal("LIVE", read.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_cancel_after_the_deadline_is_refused_by_the_database_clock()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);

        var refused = await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/funding/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task A_draft_is_cancelled_without_asking_Pledges()
    {
        var given = new Scenario(database, _host);
        var id = await given.Draft();

        var cancelled = await given.Creator().PostAsync($"/v1/campaigns/{id}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var read = await Database.Client(_host).GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", Ct);
        Assert.Equal("CANCELLED", read.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_replayed_create_returns_its_Location_too()
    {
        var given = new Scenario(database, _host);
        var key = Guid.NewGuid().ToString();
        HttpRequestMessage Create()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/campaigns")
            {
                Content = JsonContent.Create(new { title = "Replay", goal_minor = 100, currency = "USD", deadline = "2030-01-01T00:00:00Z", tiers = Array.Empty<object>() }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }

        var first = await given.Creator().SendAsync(Create(), Ct);
        var second = await given.Creator().SendAsync(Create(), Ct);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.NotNull(second.Headers.Location);
    }
}
