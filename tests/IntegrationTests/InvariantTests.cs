using System.Net.Http.Json;

using Payments.Infrastructure;

using Pledges.Infrastructure;

namespace IntegrationTests;

/// <summary>
/// The rules as gauges (CQA-010): each query must return 0, and breaking its rule behind the API's
/// back, as the owner, makes its gauge rise. A new host measures as it starts. A pledge charged twice
/// is refused by the one_success_per_pledge index even for the owner, so its gauge has no such test.
/// </summary>
public sealed class InvariantTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private async Task MeasuredAfter(Func<bool> risen)
    {
        await using var restarted = database.StartHost();
        _ = restarted.Server;
        await Scenario.Eventually(() => Task.FromResult(risen()));
    }

    private static async Task<Scenario.Published> WithOnePledge(Scenario given)
    {
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 1));
        (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000, campaign.TierIds[0]), Ct)).EnsureSuccessStatusCode();
        return campaign;
    }

    [Fact]
    public async Task A_tier_with_more_live_pledges_than_its_limit_shows_on_the_gauge()
    {
        // The counter cannot pass its limit (the never_oversell check); the pledges can, behind its back.
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 2));
        foreach (var _ in Enumerable.Range(0, 2))
        {
            (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000, campaign.TierIds[0]), Ct)).EnsureSuccessStatusCode();
        }

        await given.Execute("UPDATE pledges.tier_counters SET limit_qty = 1, claimed = 1 WHERE campaign_id = @id AND limit_qty = 2", campaign.CampaignId);
        try
        {
            await MeasuredAfter(() => PledgesInvariants.OversoldTiers.Value >= 1);
        }
        finally
        {
            await given.Execute("UPDATE pledges.tier_counters SET limit_qty = 2, claimed = 2 WHERE campaign_id = @id AND limit_qty = 1", campaign.CampaignId);
        }
    }

    [Fact]
    public async Task A_pledge_marked_for_a_charge_in_an_unfunded_campaign_shows_on_the_gauge()
    {
        var given = new Scenario(database, _host);
        var campaign = await WithOnePledge(given);
        await given.Execute("""
            UPDATE pledges.funding_windows SET status = 'UNSUCCESSFUL' WHERE campaign_id = @id;
            UPDATE pledges.pledges SET status = 'TO_CHARGE' WHERE campaign_id = @id;
            """, campaign.CampaignId);
        try
        {
            await MeasuredAfter(() => PledgesInvariants.ChargedUnfunded.Value >= 1);
        }
        finally
        {
            await given.Execute("UPDATE pledges.pledges SET status = 'RELEASED' WHERE campaign_id = @id", campaign.CampaignId);
        }
    }

    [Fact]
    public async Task A_ledger_entry_after_the_decision_shows_on_the_gauge()
    {
        var given = new Scenario(database, _host);
        var campaign = await WithOnePledge(given);
        await given.Execute("UPDATE pledges.funding_windows SET status = 'FUNDED', decided_at = now() - interval '1 hour' WHERE campaign_id = @id", campaign.CampaignId);
        try
        {
            await MeasuredAfter(() => PledgesInvariants.LedgerAfterDecision.Value >= 1);
        }
        finally
        {
            await given.Execute("UPDATE pledges.funding_windows SET decided_at = now() + interval '1 hour' WHERE campaign_id = @id", campaign.CampaignId);
        }
    }

    [Fact]
    public async Task A_charge_of_a_campaign_with_no_settlement_job_shows_on_the_gauge()
    {
        var given = new Scenario(database, _host);
        await given.Execute("""
            INSERT INTO payments.charges (pledge_id, campaign_id, backer_id, amount_minor, currency, payment_method_ref, status, attempt_no)
            VALUES (gen_random_uuid(), @id, gen_random_uuid(), 6000, 'USD', 'pm_card_visa', 'TO_CHARGE', 0);
            """, Guid.NewGuid());

        await MeasuredAfter(() => PaymentsMonitor.ChargesWithoutSettlement.Value >= 1);
    }
}
