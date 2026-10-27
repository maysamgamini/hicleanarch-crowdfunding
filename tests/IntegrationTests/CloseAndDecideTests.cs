using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Pledges.Application;
using Pledges.Infrastructure.Persistence;

namespace IntegrationTests;

/// <summary>Lessons 7 and 8, built: the close waits for pledges in flight, and the exact sum decides once.</summary>
public sealed class CloseAndDecideTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private async Task<int> ClosePass(Scenario.Published campaign)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaign.CampaignId), Ct)).Value;
    }

    private static async Task Pledge(Scenario given, Scenario.Published campaign, long amount)
    {
        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(amount), Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
    }

    private static Task PassDeadline(Scenario given, Scenario.Published campaign) =>
        given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);

    private async Task<string> Shown(Scenario.Published campaign) =>
        (await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct)).GetProperty("status").GetString()!;

    [Fact]
    public async Task A_campaign_that_reached_its_goal_is_funded_and_every_pledge_is_asked_to_be_charged()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(10_000);
        await Pledge(given, campaign, 6_000);
        await Pledge(given, campaign, 5_000);
        await PassDeadline(given, campaign);

        await ClosePass(campaign);

        Assert.Equal("FUNDED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(11_000L, await given.Scalar<long>("SELECT final_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(2L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id AND status = 'TO_CHARGE'", campaign.CampaignId));
        Assert.Equal(2L, await given.Scalar<long>("SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeChargeRequested' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        await Scenario.Eventually(async () => await Shown(campaign) == "FUNDED");
    }

    /// <summary>All or nothing: funded at or above the goal, so a sum equal to the goal is funded.</summary>
    [Fact]
    public async Task A_campaign_that_raised_exactly_its_goal_is_funded_and_every_pledge_is_asked_to_be_charged()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(10_000);
        await Pledge(given, campaign, 6_000);
        await Pledge(given, campaign, 4_000);
        await PassDeadline(given, campaign);

        await ClosePass(campaign);

        Assert.Equal("FUNDED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(10_000L, await given.Scalar<long>("SELECT final_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(2L, await given.Scalar<long>("SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeChargeRequested' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
    }

    [Fact]
    public async Task A_campaign_short_of_its_goal_is_unsuccessful_releases_its_pledges_and_charges_nobody()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(50_000);
        await Pledge(given, campaign, 6_000);
        await PassDeadline(given, campaign);

        await ClosePass(campaign);

        Assert.Equal("UNSUCCESSFUL", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id AND status = 'RELEASED'", campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeChargeRequested' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        await Scenario.Eventually(async () => await Shown(campaign) == "RELEASED");
    }

    [Fact]
    public async Task Two_passes_at_once_decide_a_campaign_exactly_once()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000);
        await Pledge(given, campaign, 6_000);
        await PassDeadline(given, campaign);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => ClosePass(campaign), Ct)));

        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.outbox WHERE type = 'CampaignFundingDecided' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeChargeRequested' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
    }

    [Fact]
    public async Task A_campaign_left_ENDED_by_a_pass_that_died_is_decided_on_the_next_pass()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000);
        await Pledge(given, campaign, 6_000);
        await given.Execute("UPDATE pledges.funding_windows SET status = 'ENDED', ended_at = now() - interval '1 minute', deadline = now() - interval '1 minute' WHERE campaign_id = @id", campaign.CampaignId);

        await ClosePass(campaign);

        Assert.Equal("FUNDED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_cancelled_campaign_is_never_closed_or_decided()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000);
        (await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();
        await PassDeadline(given, campaign);

        await ClosePass(campaign);

        Assert.Equal("CANCELLED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_pledge_that_started_before_the_deadline_is_counted_and_one_after_is_refused()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(50_000);

        // Pledge A, through the real handler, inside a transaction it keeps open past the deadline.
        await using var scopeA = _host.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var transactionA = await dbA.Database.BeginTransactionAsync(Ct);
        var placedA = await scopeA.ServiceProvider.GetRequiredService<IDispatcher>().Send<PlacePledge, PledgePlaced>(
            new PlacePledge(campaign.CampaignId, Guid.NewGuid(), null, 6_000, "pm_card_visa", $"test:{Guid.NewGuid():N}"), Ct);
        Assert.True(placedA.IsSuccess);

        // The deadline passes while A is still open: A read LIVE before it.
        await PassDeadline(given, campaign);
        // The close ends the campaign at once and waits out the request path's time limit before it
        // sums (lesson s3-l05).
        var close = Task.Run(() => ClosePass(campaign), Ct);
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId) == "ENDED");

        var late = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_000), Ct);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("campaign_closed", (await late.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());

        await transactionA.CommitAsync(Ct);
        await close;
        await Scenario.Eventually(async () =>
        {
            await ClosePass(campaign);
            return await given.Scalar<bool>("SELECT final_minor IS NOT NULL FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId);
        });

        Assert.Equal(6_000L, await given.Scalar<long>("SELECT final_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal("UNSUCCESSFUL", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }
}
