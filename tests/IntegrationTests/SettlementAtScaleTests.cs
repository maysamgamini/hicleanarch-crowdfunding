using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Payments.Application;
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
        await using var scope = host.Services.CreateAsyncScope();
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
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
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
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
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
        await using var host = Host(("Payments:Breaker:FailuresToOpen", "2"), ("Payments:Breaker:OpenFor", "00:00:00.500"));
        var (given, campaign) = await Funded(host, 2);
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
        psp.Unreachable(true);

        Assert.False(await Send<RunSettlement, bool>(host, new RunSettlement("breaker", campaign.CampaignId)));
        Assert.False(await Send<RunSettlement, bool>(host, new RunSettlement("breaker", campaign.CampaignId)));
        Assert.True(host.Services.GetRequiredService<CircuitBreaker>().IsOpen);
        var attempts = psp.Attempts;

        Assert.False(await Send<RunSettlement, bool>(host, new RunSettlement("breaker", campaign.CampaignId)));
        Assert.Equal(attempts, psp.Attempts); // open: nothing was sent, no token was spent
        Assert.Equal("PENDING", await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));

        psp.Unreachable(false);
        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);
        await Send<RunSettlement, bool>(host, new RunSettlement("breaker", campaign.CampaignId));

        Assert.Equal(2L, await Charged(given, campaign));
        Assert.False(host.Services.GetRequiredService<CircuitBreaker>().IsOpen);
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
}
