using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Notifications;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>A cancelled campaign lets every pledge go, tells every backer, and takes no pledge after the cancel.</summary>
public sealed class CancelReleaseTests(Database database) : IDisposable
{
    private readonly WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private static int SentTo(WebApplicationFactory<Program> host, Guid user, string subject) =>
        host.Services.GetRequiredService<InMemoryEmailSender>().Sent.Count(m => m.RecipientId == user && m.Subject == subject);

    private static async Task ClosePass(WebApplicationFactory<Program> host, Guid campaignId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaignId), Ct);
    }

    private static Task<long> Count(Scenario given, Guid campaignId, string status) =>
        given.Scalar<long>($"SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id AND status = '{status}'", campaignId);

    private static Task<long> LedgerSum(Scenario given, Guid campaignId) =>
        given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaignId);

    private static Task<bool> ReleaseFinished(Scenario given, Guid campaignId) =>
        given.Scalar<bool>("SELECT marking_finished_at IS NOT NULL FROM pledges.funding_windows WHERE campaign_id = @id", campaignId);

    [Fact]
    public async Task The_cancel_releases_an_active_pledge_expires_a_waiting_one_and_every_backer_is_told()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var (active, waiting) = (Guid.NewGuid(), Guid.NewGuid());
        (await given.Backer(active).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000, campaign.TierIds[0]), Ct)).EnsureSuccessStatusCode();
        var placed = await given.Backer(waiting).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = 6_000, tier_id = campaign.TierIds[0], payment_method_id = (string?)null }, Ct);
        Assert.Equal("PENDING_SETUP", (await placed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        Assert.Equal(1, await given.Scalar<int>("SELECT held FROM pledges.tier_counters WHERE tier_id = (SELECT tier_id FROM pledges.pledges WHERE campaign_id = @id AND status = 'PENDING_SETUP')", campaign.CampaignId));
        var sum = await LedgerSum(given, campaign.CampaignId);

        (await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();

        // In the cancel's own transaction, before any pass of the close job.
        Assert.Equal(1L, await Count(given, campaign.CampaignId, "RELEASED"));
        Assert.Equal(1L, await Count(given, campaign.CampaignId, "EXPIRED"));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id AND status IN ('ACTIVE', 'PENDING_SETUP')", campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT sum(held)::bigint FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT sum(claimed)::bigint FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(sum, await LedgerSum(given, campaign.CampaignId)); // as an unsuccessful decision: no entry, the sum stays what was pledged
        Assert.Equal(2L, await given.Scalar<long>("SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeReleased' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));

        await Scenario.Eventually(() => Task.FromResult(SentTo(_host, active, "A campaign you back was cancelled") == 1));
        await Scenario.Eventually(() => Task.FromResult(SentTo(_host, waiting, "A campaign you back was cancelled") == 1));
        await Scenario.Eventually(() => Task.FromResult(SentTo(_host, given.CreatorId, "Your campaign is cancelled") == 1));
    }

    [Fact]
    public async Task A_pledge_to_a_cancelled_campaign_is_refused_as_closed()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        (await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();

        var refused = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("campaign_closed", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id", campaign.CampaignId));
    }

    /// <summary>
    /// A pledge read LIVE just before the cancel committed, and committed just after it (lesson s3-l05:
    /// pledges take no window lock). The close job releases it, and finishes the release only once the
    /// fence's wait after the cancel is behind.
    /// </summary>
    [Fact]
    public async Task A_pledge_that_commits_just_after_the_cancel_is_released_and_its_backer_told()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        (await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();
        var late = Guid.NewGuid();
        await given.Execute($"""
            INSERT INTO pledges.pledges (id, campaign_id, backer_id, amount_minor, status, payment_method_ref)
            VALUES ('{Guid.CreateVersion7()}', @id, '{late}', 6000, 'ACTIVE', 'pm_card_visa')
            """, campaign.CampaignId);

        await ClosePass(_host, campaign.CampaignId);

        Assert.Equal(1L, await Count(given, campaign.CampaignId, "RELEASED"));
        Assert.False(await ReleaseFinished(given, campaign.CampaignId)); // the fence's wait after the cancel is not behind yet
        await Scenario.Eventually(() => Task.FromResult(SentTo(_host, late, "A campaign you back was cancelled") == 1));

        await given.Execute("UPDATE pledges.funding_windows SET ended_at = now() - interval '1 minute' WHERE campaign_id = @id", campaign.CampaignId);
        await ClosePass(_host, campaign.CampaignId);

        Assert.True(await ReleaseFinished(given, campaign.CampaignId));
        Assert.Equal("CANCELLED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }

    /// <summary>A large campaign: the cancel releases its first batch inside the request's time limit, the close job the rest, batch by batch.</summary>
    [Fact]
    public async Task A_large_campaigns_pledges_are_released_in_batches_by_the_close_job()
    {
        await using var host = database.StartHost().WithWebHostBuilder(web => web
            .UseSetting("Pledges:CancelReleaseBatchSize", "2")
            .UseSetting("Pledges:MarkBatchSize", "2"));
        var given = new Scenario(database, host);
        var campaign = await given.Publish();
        var backers = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var backer in backers)
        {
            (await given.Backer(backer).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        }

        (await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(2L, await Count(given, campaign.CampaignId, "RELEASED"));
        Assert.Equal(3L, await Count(given, campaign.CampaignId, "ACTIVE"));

        await given.Execute("UPDATE pledges.funding_windows SET ended_at = now() - interval '1 minute' WHERE campaign_id = @id", campaign.CampaignId);
        await ClosePass(host, campaign.CampaignId);

        Assert.Equal(5L, await Count(given, campaign.CampaignId, "RELEASED"));
        Assert.True(await ReleaseFinished(given, campaign.CampaignId));
        Assert.Equal(5L, await given.Scalar<long>("SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeReleased' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        foreach (var backer in backers)
        {
            await Scenario.Eventually(() => Task.FromResult(SentTo(host, backer, "A campaign you back was cancelled") == 1));
        }
    }
}
