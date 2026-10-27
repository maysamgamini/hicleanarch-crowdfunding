using System.Net;
using System.Net.Http.Json;

using BuildingBlocks.Application;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Payments.Application;
using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>
/// Lesson s1-a02's rules, assertions unchanged since Release 1, against two services: Payments runs only
/// in its own (lesson s4-l10; s4-l05 ran them both ways).
/// </summary>
public sealed class RuleTests(Database database) : RuleChecks(database, RuleHosts.Two(database));

/// <summary>
/// Where the rule tests send their requests (the monolith) and their Payments commands and PSP checks
/// (the monolith too, or the Payments service once it has a host of its own).
/// </summary>
public sealed record RuleHosts(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Monolith, Func<IServiceProvider> Payments, IDisposable Owner)
{
    // Rule 3 sends thirty pledges at one tier at once and expects every place taken; under a busy
    // machine some would wait longer than the 300 ms a pledge waits for a tier row in production and
    // be told to try again. The rules, not that wait, are under test here.
    private static void TierWait(IWebHostBuilder web) => web.UseSetting("Pledges:TierWait", "00:00:10");

    public static RuleHosts Two(Database database)
    {
        var hosts = new TwoHosts(database, TierWait);
        return new RuleHosts(hosts.Monolith, () => hosts.Payments.Services, hosts);
    }
}

/// <summary>
/// Lesson s1-a02's rules, one test each, named after the rule. Each drives the real system into the
/// situation that would break the rule, then asks the database the rule's own question, which must
/// have the answer "none" (lesson s1-a02's checks, scoped to the test's campaign).
/// </summary>
public abstract class RuleChecks(Database database, RuleHosts hosts) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = hosts.Monolith;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        hosts.Owner.Dispose();
        GC.SuppressFinalize(this);
    }

    private FakePaymentProvider Psp => hosts.Payments().GetRequiredService<FakePaymentProvider>();

    /// <summary>A command goes to the host that runs its module.</summary>
    private async Task Send<TCommand, T>(TCommand command)
        where TCommand : ICommand<T>
    {
        var services = typeof(TCommand).Namespace!.StartsWith("Payments.", StringComparison.Ordinal) ? hosts.Payments() : _host.Services;
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<TCommand, T>(command, Ct);
    }

    private async Task Decide(Scenario given, Scenario.Published campaign)
    {
        await PassTheDeadline(given, campaign);
        await Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaign.CampaignId));
    }

    /// <summary>The deadline is now: every pledge so far was made before it. No close pass has run.</summary>
    private static Task PassTheDeadline(Scenario given, Scenario.Published campaign) =>
        given.Execute("UPDATE pledges.funding_windows SET deadline = now() WHERE campaign_id = @id", campaign.CampaignId);

    private async Task SettleUntilDone(Scenario given, Scenario.Published campaign)
    {
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) == 1);
        // A lost reply is looked up after the settings' back-off, a few seconds: passes go on until then.
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        for (var i = 0; DateTimeOffset.UtcNow < until && await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) != "DONE"; i++)
        {
            await Send<RunSettlement, bool>(new RunSettlement($"rules-{i}", campaign.CampaignId));
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }
    }

    private static async Task PledgeWithCard(Scenario given, Scenario.Published campaign, Guid backer, long amount = 6_000, string card = FakePaymentProvider.CardSucceeds) =>
        (await given.Backer(backer).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = amount, tier_id = (string?)null, payment_method_id = card }, Ct)).EnsureSuccessStatusCode();

    [Fact]
    public async Task Rule_1_nobody_is_charged_for_an_unfunded_campaign()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(1_000_000);
        await PledgeWithCard(given, campaign, Guid.NewGuid());
        await PledgeWithCard(given, campaign, Guid.NewGuid());
        var before = Psp.ChargesMade;

        await Decide(given, campaign);
        await Task.Delay(500, Ct); // anything the decision set in motion has had time to run

        Assert.Equal("UNSUCCESSFUL", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(before, Psp.ChargesMade);
        Assert.Equal(0L, await given.Scalar<long>(
            "SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeChargeRequested' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("""
            SELECT count(*) FROM payments.charges c
            JOIN pledges.funding_windows w ON w.campaign_id = c.campaign_id
            WHERE c.campaign_id = @id AND w.status <> 'FUNDED'
            """, campaign.CampaignId));
    }

    [Fact]
    public async Task Rule_2_each_pledge_of_a_funded_campaign_is_charged_at_most_once()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(1_000);
        for (var i = 0; i < 4; i++)
        {
            await PledgeWithCard(given, campaign, Guid.NewGuid());
        }

        await Decide(given, campaign);
        var before = Psp.ChargesMade;
        Psp.LoseNextReplies(2); // two charges happen and their replies are lost
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId) == 4);
        await Task.WhenAll(Task.Run(() => SettleUntilDone(given, campaign), Ct), Task.Run(() => SettleUntilDone(given, campaign), Ct));
        await SettleUntilDone(given, campaign);

        Assert.Equal(4L, await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id AND status = 'CHARGED'", campaign.CampaignId));
        Assert.Equal(before + 4, Psp.ChargesMade); // four cards, four charges at the PSP: none twice
        Assert.Equal(0L, await given.Scalar<long>("""
            SELECT count(*) FROM (
                SELECT a.pledge_id FROM payments.charge_attempts a JOIN payments.charges c USING (pledge_id)
                WHERE c.campaign_id = @id AND a.status = 'SUCCEEDED'
                GROUP BY a.pledge_id HAVING count(*) > 1) twice
            """, campaign.CampaignId));
    }

    [Fact]
    public async Task Rule_3_a_limited_tier_never_sells_more_than_its_limit()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 5));

        // Saved cards and new cards at once: claims and holds compete for the same five places. A
        // pledge told the tier is busy is sent again, as a client does.
        var answers = await Task.WhenAll(Enumerable.Range(0, 30).Select(async i =>
        {
            using var response = await Scenario.SendUntilAnswered(given.Backer(Guid.NewGuid()), () =>
                new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/pledges")
                {
                    Content = JsonContent.Create(new { amount_minor = 4_900, tier_id = campaign.TierIds[0], payment_method_id = i % 2 == 0 ? FakePaymentProvider.CardSucceeds : null }),
                });
            return response.StatusCode;
        }));

        Assert.Equal(5, answers.Count(a => a == HttpStatusCode.Created));

        Assert.Equal(0L, await given.Scalar<long>("""
            SELECT count(*) FROM (
                SELECT t.tier_id FROM pledges.tier_counters t
                JOIN pledges.pledges p ON p.tier_id = t.tier_id AND p.status IN ('PENDING_SETUP', 'ACTIVE')
                WHERE t.campaign_id = @id AND t.limit_qty IS NOT NULL
                GROUP BY t.tier_id, t.limit_qty HAVING count(*) > t.limit_qty) oversold
            """, campaign.CampaignId));
        Assert.Equal(5, await given.Scalar<int>("SELECT claimed + held FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task Rule_4_no_pledge_after_the_deadline_and_none_left_out_of_the_decision()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(1_000);
        await PledgeWithCard(given, campaign, Guid.NewGuid());
        await PledgeWithCard(given, campaign, Guid.NewGuid());
        await PassTheDeadline(given, campaign);

        // After the deadline, before any close pass: the funding window still says LIVE, and the
        // database's clock alone refuses the pledge.
        var late = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("LIVE", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));

        await Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("""
            SELECT count(*) FROM pledges.funding_windows w
            WHERE w.campaign_id = @id AND (w.final_minor IS NULL
               OR w.final_minor <> (SELECT coalesce(sum(delta_minor), 0) FROM pledges.pledge_ledger l WHERE l.campaign_id = w.campaign_id))
            """, campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("""
            SELECT count(*) FROM pledges.pledge_ledger l JOIN pledges.funding_windows w USING (campaign_id)
            WHERE l.campaign_id = @id AND l.created_at >= w.deadline
            """, campaign.CampaignId));
    }

    [Fact]
    public async Task Rule_5_a_backer_has_at_most_one_live_pledge_per_campaign()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());

        // Two tabs, ten times: ten different keys at once.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(0L, await given.Scalar<long>("""
            SELECT count(*) FROM (
                SELECT backer_id FROM pledges.pledges WHERE campaign_id = @id AND status IN ('PENDING_SETUP', 'ACTIVE')
                GROUP BY backer_id HAVING count(*) > 1) twice
            """, campaign.CampaignId));
    }
}
