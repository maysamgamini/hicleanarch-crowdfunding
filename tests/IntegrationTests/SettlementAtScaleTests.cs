using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Identity;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Payments.Application;
using Payments.Infrastructure.Persistence.Migrations;
using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>Lesson s3-l08, built: marking in batches, turns across campaigns, one budget, and a breaker.</summary>
public sealed class SettlementAtScaleTests(Database database, Valkey valkey) : IClassFixture<Valkey>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A host with small batches and its own buckets, so the effects show with a few pledges.</summary>
    private WebApplicationFactory<Program> Host(params (string Key, string Value)[] settings) =>
        database.StartHost().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:Valkey", valkey.Configuration)
                .UseSetting("Payments:Budget:Prefix", $"tests:{Guid.NewGuid():N}")
                .UseSetting("Payments:Settlement:BatchSize", "2")
                .UseSetting("Pledges:MarkBatchSize", "2");
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }
        });

    private static async Task<T> Send<TCommand, T>(WebApplicationFactory<Program> host, TCommand command)
        where TCommand : ICommand<T>
    {
        await using var scope = host.ServicesFor<TCommand>().CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<TCommand, T>(command, Ct)).Value;
    }

    private static Task<int> Close(WebApplicationFactory<Program> host, Scenario.Published campaign) =>
        Send<CloseDueCampaigns, int>(host, new CloseDueCampaigns(campaign.CampaignId));

    /// <summary>A campaign past its deadline with <paramref name="pledges"/> pledges on saved cards, not yet closed.</summary>
    private async Task<(Scenario Given, Scenario.Published Campaign)> Due(WebApplicationFactory<Program> host, int pledges)
    {
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        for (var i = 0; i < pledges; i++)
        {
            var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
                new { amount_minor = 6_000, tier_id = (string?)null, payment_method_id = FakePaymentProvider.CardSucceeds }, Ct);
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        }

        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        return (given, campaign);
    }

    /// <summary>Closed, decided, every pledge marked, and every charge and the job created in Payments.</summary>
    private async Task<(Scenario Given, Scenario.Published Campaign)> Funded(WebApplicationFactory<Program> host, int pledges)
    {
        var (given, campaign) = await Due(host, pledges);
        do
        {
            await Close(host, campaign);
        }
        while (await given.Scalar<long>("SELECT count(*) FROM pledges.funding_windows WHERE campaign_id = @id AND marking_finished_at IS NULL", campaign.CampaignId) == 1);

        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId) == pledges
            && await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) == 1);
        return (given, campaign);
    }

    private static Task<long> Charged(Scenario given, Scenario.Published campaign) =>
        given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id AND status = 'CHARGED'", campaign.CampaignId);

    [Fact]
    public async Task Pledges_are_marked_in_batches_and_the_next_pass_resumes_from_the_cursor()
    {
        await using var host = Host(("Pledges:MarkBatchesPerPass", "1"));
        var (given, campaign) = await Due(host, 5);
        Task<long> Marked() => given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id AND status = 'TO_CHARGE'", campaign.CampaignId);
        Task<long> Requested() => given.Scalar<long>(
            "SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeChargeRequested' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId);

        Assert.Equal(1, await Close(host, campaign)); // decided, and the first batch marked
        Assert.Equal(2L, await Marked());
        Assert.Equal("FUNDED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.funding_windows WHERE campaign_id = @id AND marked_through IS NOT NULL AND marking_finished_at IS NULL", campaign.CampaignId));

        // As if the task had died: the next passes pick the campaign up from its cursor.
        Assert.Equal(0, await Close(host, campaign));
        Assert.Equal(4L, await Marked());
        Assert.Equal(0, await Close(host, campaign));
        Assert.Equal(5L, await Marked());

        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.funding_windows WHERE campaign_id = @id AND marking_finished_at IS NOT NULL", campaign.CampaignId));
        Assert.Equal(5L, await Requested()); // each pledge asked for once
        Assert.Equal(0, await Close(host, campaign));
        Assert.Equal(5L, await Requested());
    }

    [Fact]
    public async Task Campaigns_take_turns_so_a_small_one_is_not_left_behind_a_big_one()
    {
        await using var host = Host();
        var (bigGiven, big) = await Funded(host, 6);
        var (smallGiven, small) = await Funded(host, 2);
        Guid[] both = [big.CampaignId, small.CampaignId];

        Assert.True(await Send<RunSettlement, bool>(host, new RunSettlement("turns", Among: both))); // the older job: 2 of the big one's 6
        Assert.True(await Send<RunSettlement, bool>(host, new RunSettlement("turns", Among: both))); // then the small one's turn

        Assert.Equal(2L, await Charged(bigGiven, big));
        Assert.Equal(2L, await Charged(smallGiven, small));
        Assert.Equal("DONE", await smallGiven.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", small.CampaignId));

        while (await Send<RunSettlement, bool>(host, new RunSettlement("turns", Among: both)))
        {
        }

        Assert.Equal(6L, await Charged(bigGiven, big));
    }

    [Fact]
    public async Task The_shared_budget_holds_charges_to_the_PSPs_rate()
    {
        await using var host = Host(("Payments:Budget:ChargesPerSecond", "4"), ("Payments:Settlement:BatchSize", "100"));
        var (given, campaign) = await Funded(host, 10);
        var psp = host.Payments().Services.GetRequiredService<FakePaymentProvider>();
        var started = DateTimeOffset.UtcNow;

        await Send<RunSettlement, bool>(host, new RunSettlement("budget", campaign.CampaignId));

        Assert.Equal(10L, await Charged(given, campaign));
        var charges = psp.Calls.Where(c => c.Call == "charge" && c.At >= started).Select(c => c.At).Order().ToList();
        Assert.Equal(10, charges.Count);
        // A full bucket holds 4; after that, 4 a second: no second ever sees more than 4 + 4.
        Assert.All(charges, at => Assert.InRange(charges.Count(c => c >= at && c < at.AddSeconds(1)), 1, 8));
        Assert.True(charges[^1] - charges[0] >= TimeSpan.FromSeconds(1.4)); // (10 - 4) / 4 seconds, less the scheduler's jitter
    }

    [Fact]
    public async Task A_429_empties_the_bucket_and_the_same_call_is_sent_again_later()
    {
        await using var host = Host(("Payments:Budget:ChargesPerSecond", "4"));
        var (given, campaign) = await Funded(host, 1);
        var psp = host.Payments().Services.GetRequiredService<FakePaymentProvider>();
        var before = psp.ChargesMade;
        var started = DateTimeOffset.UtcNow;
        psp.RateLimitNext("charge", 1);

        await Send<RunSettlement, bool>(host, new RunSettlement("limited", campaign.CampaignId));

        Assert.Equal(1L, await Charged(given, campaign));
        Assert.Equal(before + 1, psp.ChargesMade);
        var charges = psp.Calls.Where(c => c.Call == "charge" && c.At >= started).Select(c => c.At).ToList();
        Assert.Equal(2, charges.Count); // refused, then sent again
        Assert.True(charges[1] - charges[0] >= TimeSpan.FromMilliseconds(200)); // after a refill at 4 a second
    }

    [Fact]
    public async Task The_breaker_opens_when_the_PSP_fails_and_settlement_pauses_without_calling_it()
    {
        // Open long enough that the passes below run inside it on a loaded machine.
        await using var host = Host(("Payments:Breaker:MinimumThroughput", "2"), ("Payments:Breaker:OpenFor", "00:00:03"), ("Payments:Breaker:OpenJitter", "0"));
        var (given, campaign) = await Funded(host, 2);
        var psp = host.Payments().Services.GetRequiredService<FakePaymentProvider>();
        psp.Unreachable(true);

        Assert.False(await Send<RunSettlement, bool>(host, new RunSettlement("breaker", campaign.CampaignId)));
        Assert.False(await Send<RunSettlement, bool>(host, new RunSettlement("breaker", campaign.CampaignId)));
        Assert.True(host.Payments().Services.GetRequiredService<CircuitBreaker>().IsOpen);
        var attempts = psp.Attempts;

        Assert.False(await Send<RunSettlement, bool>(host, new RunSettlement("breaker", campaign.CampaignId)));
        Assert.Equal(attempts, psp.Attempts); // open: nothing was sent, no token was spent
        Assert.Equal("PENDING", await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));

        psp.Unreachable(false);
        await Task.Delay(TimeSpan.FromSeconds(3.2), Ct);
        // Half-open, one trial call goes through and closes the circuit; the charges it refused meanwhile go on the next turn.
        for (var pass = 0; pass < 5 && await Charged(given, campaign) < 2; pass++)
        {
            Assert.True(await Send<RunSettlement, bool>(host, new RunSettlement("breaker", campaign.CampaignId)));
        }

        Assert.Equal(2L, await Charged(given, campaign));
        Assert.False(host.Payments().Services.GetRequiredService<CircuitBreaker>().IsOpen);
    }

    [Fact]
    public async Task Progress_shows_counts_and_an_eta_at_the_charges_rate()
    {
        await using var host = Host(("Payments:Budget:ChargesPerSecond", "2"), ("Payments:Settlement:BatchSize", "100"));
        var (given, campaign) = await Funded(host, 4);
        var creator = given.Creator();

        var before = await creator.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}/settlement", Ct);
        Assert.Equal(4, before.GetProperty("expected").GetInt32());
        Assert.Equal(4, before.GetProperty("open").GetInt32());
        Assert.True(before.GetProperty("eta_seconds").GetInt32() >= 2); // 4 charges at 2 a second, at least

        await Send<RunSettlement, bool>(host, new RunSettlement("progress", campaign.CampaignId));

        var after = await creator.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}/settlement", Ct);
        Assert.Equal("DONE", after.GetProperty("status").GetString());
        Assert.Equal(4, after.GetProperty("charged").GetInt32());
        Assert.Equal(0, after.GetProperty("eta_seconds").GetInt32());
    }

    [Fact]
    public async Task Settlement_progress_is_only_for_the_campaigns_own_creator()
    {
        await using var host = Host();
        var (given, campaign) = await Funded(host, 1);
        var url = $"/v1/campaigns/{campaign.Id}/settlement";
        var anotherCreator = Database.Client(host, Database.Token(Guid.NewGuid(), Roles.Creator));
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM payments.campaign_payees WHERE campaign_id = @id", campaign.CampaignId) == 1);

        var owner = await given.Creator().GetAsync(url, Ct);
        var someoneElse = await anotherCreator.GetAsync(url, Ct);
        var backer = await given.Backer(Guid.NewGuid()).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal(1, (await owner.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("expected").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, someoneElse.StatusCode); // as if there were no settlement: nothing about another creator's campaign
        Assert.Equal("settlement_not_found", (await someoneElse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, backer.StatusCode);
    }

    [Fact]
    public async Task A_campaign_published_before_payees_were_recorded_gets_its_payee_from_the_backfill()
    {
        await using var host = Host();
        var (given, campaign) = await Funded(host, 1);
        var url = $"/v1/campaigns/{campaign.Id}/settlement";
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM payments.campaign_payees WHERE campaign_id = @id", campaign.CampaignId) == 1);
        await given.Execute("DELETE FROM payments.campaign_payees WHERE campaign_id = @id", campaign.CampaignId); // as before s3-l15

        Assert.Equal(HttpStatusCode.NotFound, (await given.Creator().GetAsync(url, Ct)).StatusCode); // no payee: nobody is its creator yet

        await given.Execute(BackfillCampaignPayees.Backfill, campaign.CampaignId);

        Assert.Equal(given.CreatorId, await given.Scalar<Guid>("SELECT creator_id FROM payments.campaign_payees WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(HttpStatusCode.OK, (await given.Creator().GetAsync(url, Ct)).StatusCode);
    }

    /// <summary>Passes of one worker at one campaign until its job is done, as a task's worker would make them.</summary>
    private static async Task<int> WorkUntilDone(WebApplicationFactory<Program> host, Scenario given, string worker, Guid? campaignId = null, IReadOnlyCollection<Guid>? among = null)
    {
        var worked = 0;
        var until = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < until && await OpenOf(given, campaignId, among) > 0)
        {
            if (await Send<RunSettlement, bool>(host, new RunSettlement(worker, campaignId, among)))
            {
                worked++;
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            }
        }

        return worked;
    }

    private static async Task<long> OpenOf(Scenario given, Guid? campaignId, IReadOnlyCollection<Guid>? among)
    {
        var open = 0L;
        foreach (var id in campaignId is { } one ? [one] : among ?? [])
        {
            open += await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id AND status <> 'DONE'", id);
        }

        return open;
    }

    [Fact]
    public async Task Two_workers_share_one_campaign_and_charge_at_the_budgets_rate_not_at_one_workers_latency()
    {
        const int pledges = 60, perSecond = 20, inFlightPerWorker = 10;
        var latency = TimeSpan.FromMilliseconds(500); // the loop's own figure for a PSP call
        await using var host = Host(("Payments:Budget:ChargesPerSecond", $"{perSecond}"), ("Payments:Settlement:BatchSize", "15"),
            ("Payments:Settlement:CallsInFlight", $"{inFlightPerWorker}"), ("Pledges:MarkBatchSize", "100"));
        var (given, campaign) = await Funded(host, pledges);
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
        psp.TakesToAnswer(latency);
        var started = DateTimeOffset.UtcNow;

        var both = await Task.WhenAll(
            Task.Run(() => WorkUntilDone(host, given, "share-1", campaign.CampaignId), Ct),
            Task.Run(() => WorkUntilDone(host, given, "share-2", campaign.CampaignId), Ct));

        Assert.Equal((long)pledges, await Charged(given, campaign));
        var charges = psp.Calls.Where(c => c.Call == "charge" && c.At >= started).Select(c => c.At).Order().ToList();
        Assert.Equal(pledges, charges.Count); // each pledge once, whichever worker had it
        Assert.All(both, worked => Assert.True(worked > 0));
        // Both workers had calls in flight at the same campaign at once: more than one worker's own cap.
        Assert.True(psp.MostInFlight > inFlightPerWorker, $"at most {psp.MostInFlight} in flight");
        // The budget holds: a full bucket's second's worth, then its rate.
        Assert.All(charges, at => Assert.InRange(charges.Count(c => c >= at && c < at.AddSeconds(1)), 1, 2 * perSecond));
        // Beyond the full bucket's second's worth, the charges came at the bucket's rate. One worker
        // charging one call at a time would manage 1 / 0.5 s = 2 a second, whatever the budget allows.
        var elapsed = (charges[^1] - charges[0]).TotalSeconds;
        var rate = (pledges - perSecond) / elapsed;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"charged={pledges} elapsed={elapsed:F2}s rate={rate:F1}/s budget={perSecond}/s mostInFlight={psp.MostInFlight} passes={both[0]}+{both[1]}");
        Assert.InRange(rate, 0.6 * perSecond, 1.2 * perSecond);
    }

    [Fact]
    public async Task Two_settlements_on_two_workers_share_one_budget()
    {
        await using var host = Host(("Payments:Budget:ChargesPerSecond", "4"), ("Payments:Settlement:BatchSize", "3"));
        var (firstGiven, first) = await Funded(host, 6);
        var (secondGiven, second) = await Funded(host, 6);
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
        Guid[] both = [first.CampaignId, second.CampaignId];
        var started = DateTimeOffset.UtcNow;

        await Task.WhenAll(
            Task.Run(() => WorkUntilDone(host, firstGiven, "budget-1", among: both), Ct),
            Task.Run(() => WorkUntilDone(host, secondGiven, "budget-2", among: both), Ct));

        Assert.Equal(6L, await Charged(firstGiven, first));
        Assert.Equal(6L, await Charged(secondGiven, second));
        var charges = psp.Calls.Where(c => c.Call == "charge" && c.At >= started).Select(c => c.At).Order().ToList();
        Assert.Equal(12, charges.Count);
        // Together, never more than the one bucket allows: 4 at once, then 4 a second.
        Assert.All(charges, at => Assert.InRange(charges.Count(c => c >= at && c < at.AddSeconds(1)), 1, 8));
        Assert.True(charges[^1] - charges[0] >= TimeSpan.FromSeconds(1.4)); // (12 - 4) / 4 seconds, less the scheduler's jitter
    }

    [Fact]
    public async Task After_the_PSP_fails_no_worker_takes_the_job_again_before_the_pause()
    {
        await using var host = Host(("Payments:Settlement:PauseWhenPspFails", "01:00:00"));
        var (given, campaign) = await Funded(host, 2);
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
        Guid[] only = [campaign.CampaignId];
        psp.Unreachable(true);

        Assert.False(await Send<RunSettlement, bool>(host, new RunSettlement("pause-1", Among: only)));
        psp.Unreachable(false);
        var attempts = psp.Attempts;

        Assert.False(await Send<RunSettlement, bool>(host, new RunSettlement("pause-2", Among: only))); // the PSP is back, but the pause holds
        Assert.Equal(attempts, psp.Attempts);
        Assert.Equal(0L, await Charged(given, campaign));
        Assert.True(await given.Scalar<bool>("SELECT paused_until > now() + interval '59 minutes' FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task The_eta_counts_every_settling_campaigns_turns_up_to_its_own_charges()
    {
        await using var host = Host(("Payments:Budget:ChargesPerSecond", "2"));
        var (bigGiven, big) = await Funded(host, 4);
        var (smallGiven, small) = await Funded(host, 2);

        static async Task<int> Eta(Scenario given, Scenario.Published campaign) =>
            (await given.Creator().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}/settlement", Ct)).GetProperty("eta_seconds").GetInt32();

        var bigEta = await Eta(bigGiven, big);
        var smallEta = await Eta(smallGiven, small);

        // Turns in rotation at 2 a second: the small one is done when the big one has had 2 of its
        // charges as well, (2 + 2) / 2; the big one after all 6, 6 / 2. Other tests' settlements add to both.
        Assert.True(smallEta >= 2, $"small {smallEta}");
        Assert.True(bigEta >= 3, $"big {bigEta}");
        Assert.True(smallEta < bigEta, $"small {smallEta}, big {bigEta}"); // the big one's charges beyond the small one's count only for the big one
    }

    [Fact]
    public async Task A_worker_whose_charges_were_taken_over_makes_no_further_PSP_call_for_them()
    {
        await using var host = Host(("Payments:Settlement:BatchSize", "3"), ("Payments:Settlement:CallsInFlight", "1"));
        var (given, campaign) = await Funded(host, 3);
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
        psp.TakesToAnswer(TimeSpan.FromSeconds(1));
        var before = psp.Calls.Count(c => c.Call == "charge");

        var pass = Task.Run(() => Send<RunSettlement, bool>(host, new RunSettlement("slow", campaign.CampaignId)), Ct);
        await Scenario.Eventually(() => Task.FromResult(psp.InFlight == 1));
        // Its first call is in progress; another worker takes over the two charges it has not reached.
        await given.Execute("UPDATE payments.charges SET claimed_by = 'other', claimed_until = now() + interval '1 minute' WHERE campaign_id = @id AND status = 'TO_CHARGE'", campaign.CampaignId);
        await pass;

        Assert.Equal(before + 1, psp.Calls.Count(c => c.Call == "charge"));
        Assert.Equal(1L, await Charged(given, campaign));
        Assert.Equal(2L, await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id AND status = 'TO_CHARGE' AND claimed_by = 'other'", campaign.CampaignId));
    }
}
