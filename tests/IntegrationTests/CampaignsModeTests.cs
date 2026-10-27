using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l08: Campaigns.Host runs beside the monolith and reads the same campaigns queue; then the
/// monolith sets Modules:Campaigns:Mode = Remote, only once no outbox row here owes Campaigns an
/// in-process delivery. Rollback: InProcess.
/// </summary>
public sealed class CampaignsModeTests(Database database) : IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private WebApplicationFactory<Program> Host(IReadOnlyDictionary<string, string?> broker, CampaignsAt campaigns)
    {
        var host = database.StartHost(broker, campaigns: campaigns);
        _hosts.Add(host);
        _ = host.Services; // started
        return host;
    }

    [Fact]
    public async Task Two_hosts_reading_the_campaigns_queue_record_each_cancel_once()
    {
        var broker = database.Provision().Settings;
        var both = Host(broker, CampaignsAt.Both);
        var given = new Scenario(database, both);
        var campaigns = new List<Scenario.Published>();
        for (var i = 0; i < 4; i++)
        {
            campaigns.Add(await given.Publish());
        }

        // Pledges' FundingCancelled goes to the campaigns queue, which both hosts read; the inbox lets one record it.
        foreach (var campaign in campaigns)
        {
            await given.CancelLive(campaign);
        }

        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // a second recording would have been made by now
        foreach (var campaign in campaigns)
        {
            Assert.Equal(1L, await given.Scalar<long>(
                "SELECT count(*) FROM campaigns.outbox WHERE type = 'CampaignCancelled' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        }
    }

    [Fact]
    public async Task Remote_mode_starts_only_once_no_outbox_row_owes_Campaigns_an_in_process_delivery()
    {
        // A row only Campaigns could take in process: no host has a handler for its type, so none delivers it.
        var id = Guid.CreateVersion7();
        await database.Execute($"""
            INSERT INTO pledges.outbox (id, type, version, occurred_at, payload, attempts, sinks)
            VALUES ('{id}', 'CampaignsModeTestEvent', 1, now(), jsonb_build_object(), 0, ARRAY['Campaigns'])
            """);
        var broker = database.Provision().Settings;

        var refused = Assert.ThrowsAny<Exception>(() => Host(broker, CampaignsAt.Service));
        await database.Execute($"UPDATE pledges.outbox SET delivered_at = now() WHERE id = '{id}'");
        var remote = Host(broker, CampaignsAt.Service);

        Assert.Contains("This host no longer runs Campaigns, but outbox rows still owe it an in-process delivery", refused.ToString(), StringComparison.Ordinal);
        Assert.NotNull(remote.Services);
    }

    [Fact]
    public async Task With_Campaigns_remote_every_rule_still_holds_across_the_two_hosts()
    {
        var remote = Host(database.Provision().Settings, CampaignsAt.Service);
        var given = new Scenario(database, remote);

        // The monolith serves none of Campaigns' endpoints; through the gateway's routes the service does.
        var unknown = await Database.Client(remote).GetAsync($"/v1/campaigns/cmp_{Guid.NewGuid():N}", Ct);
        Assert.Equal("campaign_not_found", (await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        var monolith = remote.CreateClient();
        monolith.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Database.Token(BuildingBlocks.Infrastructure.Identity.Roles.Creator));
        var atMonolith = await monolith.PostAsJsonAsync("/v1/campaigns", new { }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, atMonolith.StatusCode);

        // Publish at Campaigns, pledge at Pledges through the publish gap, cancel at Pledges: the catalog follows.
        var campaign = await given.Publish();
        var (pledged, _) = await Scenario.SendThroughPublishGap(given.Backer(Guid.NewGuid()), campaign.Id,
            () => new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/pledges") { Content = JsonContent.Create(Scenario.Pledge(6_000)) });
        Assert.Equal(HttpStatusCode.Created, pledged.StatusCode);
        await given.CancelLive(campaign);

        var page = await Database.Client(remote).GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct);
        Assert.Equal("CANCELLED", page.GetProperty("status").GetString());
        Assert.Equal("RELEASED", await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task Back_to_InProcess_the_events_queued_for_Campaigns_are_each_handled_once()
    {
        var broker = database.Provision().Settings;
        var remote = Host(broker, CampaignsAt.Service);
        var given = new Scenario(database, remote);
        var campaign = await given.Publish();
        // Stop the Campaigns service: nothing reads the campaigns queue. Its last long poll (2 s) may still be
        // open at the broker, which would hand a message to a reader that is gone; let it end first.
        await remote.Campaigns()!.Host.DisposeAsync();
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        var (cancelled, _) = await Scenario.SendThroughPublishGap(given.Creator(), campaign.Id,
            () => new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/funding/cancel"));
        cancelled.EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal("LIVE", await given.Scalar<string>("SELECT status FROM campaigns.campaigns WHERE id = @id", campaign.CampaignId));

        // The rollback: the monolith runs Campaigns again and reads its queue.
        Host(broker, CampaignsAt.Monolith);
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM campaigns.campaigns WHERE id = @id", campaign.CampaignId) == "CANCELLED");
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(1L, await given.Scalar<long>(
            "SELECT count(*) FROM campaigns.outbox WHERE type = 'CampaignCancelled' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
    }
}
