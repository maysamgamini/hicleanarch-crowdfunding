using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Payments.Application;
using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>Lesson 8, built: every pledge of a funded campaign is charged at most once, whatever fails.</summary>
public sealed class SettlementTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private FakePaymentProvider Psp => _host.Services.GetRequiredService<FakePaymentProvider>();

    private async Task<T> Send<TCommand, T>(TCommand command)
        where TCommand : ICommand<T>
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<TCommand, T>(command, Ct)).Value;
    }

    private Task<bool> Settle(Scenario.Published campaign, string worker = "test-worker") =>
        Send<RunSettlement, bool>(new RunSettlement(worker, campaign.CampaignId));

    /// <summary>A funded campaign with one pledge per card, decided, with its charges and its job created.</summary>
    private async Task<(Scenario Given, Scenario.Published Campaign)> Funded(params string[] cards)
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(1_000);
        foreach (var card in cards)
        {
            var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
                new { amount_minor = 6_000, tier_id = (string?)null, payment_method_id = card }, Ct);
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        }

        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await Send<CloseDueCampaigns, int>(new CloseDueCampaigns());
        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId) == cards.Length
            && await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) == 1);
        return (given, campaign);
    }

    private static Task<long> Count(Scenario given, Scenario.Published campaign, string status) =>
        given.Scalar<long>($"SELECT count(*) FROM payments.charges WHERE campaign_id = @id AND status = '{status}'", campaign.CampaignId);

    [Fact]
    public async Task A_funded_campaign_is_charged_once_per_pledge_and_settles()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardSucceeds, FakePaymentProvider.CardSucceeds);
        var before = Psp.ChargesMade;

        Assert.True(await Settle(campaign));

        Assert.Equal(2L, await Count(given, campaign, "CHARGED"));
        Assert.Equal(before + 2, Psp.ChargesMade);
        Assert.Equal("DONE", await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));
        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id AND status = 'CHARGED'", campaign.CampaignId) == 2);
        await Scenario.Eventually(async () =>
            (await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct)).GetProperty("status").GetString() == "SETTLED");
    }

    [Fact]
    public async Task A_timeout_is_resolved_by_asking_about_the_same_attempt_and_never_charges_twice()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardSucceeds);
        var before = Psp.ChargesMade;
        Psp.LoseNextReplies(1); // the PSP charges, and its reply never arrives

        await Settle(campaign);

        Assert.Equal(1L, await Count(given, campaign, "CHARGING"));
        Assert.Equal("IN_FLIGHT", await given.Scalar<string>("SELECT a.status FROM payments.charge_attempts a JOIN payments.charges c USING (pledge_id) WHERE c.campaign_id = @id", campaign.CampaignId));

        await Settle(campaign);

        Assert.Equal(1L, await Count(given, campaign, "CHARGED"));
        Assert.Equal(before + 1, Psp.ChargesMade);
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.charge_attempts a JOIN payments.charges c USING (pledge_id) WHERE c.campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_worker_that_died_after_starting_an_attempt_leads_to_no_double_charge()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardSucceeds, FakePaymentProvider.CardSucceeds);
        var before = Psp.ChargesMade;

        // Worker A claimed the job, started attempt 1 of one pledge and died before calling the PSP;
        // for the other, it called the PSP and died before recording the answer.
        await given.Execute("UPDATE payments.settlement_jobs SET status = 'RUNNING', lease_owner = 'dead-worker', lease_until = now() + interval '1 minute' WHERE campaign_id = @id", campaign.CampaignId);
        var pledges = await PledgesOf(given, campaign);
        await StartAttemptBehindTheWorkersBack(given, pledges[0]);
        await StartAttemptBehindTheWorkersBack(given, pledges[1]);
        await Psp.Charge(new ChargeRequest($"cus_unknown", FakePaymentProvider.CardSucceeds, 6_000, "USD", BuildingBlocks.Domain.PublicId.Format("plg", pledges[1]), 1),
            $"{BuildingBlocks.Domain.PublicId.Format("plg", pledges[1])}:charge:1", Ct);

        Assert.False(await Settle(campaign, "worker-b")); // A's lease still holds: B skips the job
        await given.Execute("UPDATE payments.settlement_jobs SET lease_until = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        Assert.True(await Settle(campaign, "worker-b"));

        Assert.Equal(2L, await Count(given, campaign, "CHARGED"));
        Assert.Equal(before + 2, Psp.ChargesMade); // one charge each: re-sending A's key got the PSP's stored result
    }

    [Fact]
    public async Task Two_workers_at_once_charge_each_pledge_once()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardSucceeds, FakePaymentProvider.CardSucceeds, FakePaymentProvider.CardSucceeds);
        var before = Psp.ChargesMade;

        await Task.WhenAll(Task.Run(() => Settle(campaign, "worker-a"), Ct), Task.Run(() => Settle(campaign, "worker-b"), Ct));
        while (await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) != "DONE")
        {
            await Settle(campaign, "worker-c");
        }

        Assert.Equal(3L, await Count(given, campaign, "CHARGED"));
        Assert.Equal(before + 3, Psp.ChargesMade);
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.outbox WHERE type = 'CampaignSettled' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
    }

    [Fact]
    public async Task A_declined_card_gets_three_attempts_with_three_keys_then_fails()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardDeclined);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await given.Execute("UPDATE payments.charges SET next_attempt_at = NULL WHERE campaign_id = @id", campaign.CampaignId);
            await Settle(campaign);
        }

        Assert.Equal(1L, await Count(given, campaign, "FAILED"));
        Assert.Equal(3L, await given.Scalar<long>("SELECT count(DISTINCT a.idempotency_key) FROM payments.charge_attempts a JOIN payments.charges c USING (pledge_id) WHERE c.campaign_id = @id AND a.status = 'FAILED'", campaign.CampaignId));
        Assert.Equal("DONE", await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));
        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id AND status = 'FAILED'", campaign.CampaignId) == 1);
    }

    [Fact]
    public async Task A_declined_charge_waits_before_its_next_attempt()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardDeclined);

        await Settle(campaign);
        await Settle(campaign);

        Assert.Equal(1L, await Count(given, campaign, "TO_CHARGE"));
        Assert.Equal(1, await given.Scalar<int>("SELECT attempt_no FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));
        Assert.True(await given.Scalar<bool>("SELECT next_attempt_at > now() + interval '11 hours' FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task The_database_refuses_a_second_successful_attempt_for_one_pledge()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardSucceeds);
        await Settle(campaign);
        var pledge = (await PledgesOf(given, campaign))[0];

        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => given.Execute(
            $"INSERT INTO payments.charge_attempts (pledge_id, attempt_no, idempotency_key, status) VALUES (@id, 2, 'x:{Guid.NewGuid():N}', 'SUCCEEDED')", pledge));
        Assert.Equal("one_success_per_pledge", refused.ConstraintName);
    }

    private static async Task<List<Guid>> PledgesOf(Scenario given, Scenario.Published campaign)
    {
        var ids = new List<Guid>();
        await using var connection = new Npgsql.NpgsqlConnection(given.OwnerConnection);
        await connection.OpenAsync(Ct);
        await using var read = new Npgsql.NpgsqlCommand("SELECT pledge_id FROM payments.charges WHERE campaign_id = @id ORDER BY pledge_id", connection);
        read.Parameters.AddWithValue("id", campaign.CampaignId);
        await using var reader = await read.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static Task StartAttemptBehindTheWorkersBack(Scenario given, Guid pledgeId) =>
        given.Execute($"""
            UPDATE payments.charges SET status = 'CHARGING', attempt_no = 1 WHERE pledge_id = @id;
            INSERT INTO payments.charge_attempts (pledge_id, attempt_no, idempotency_key, status)
            VALUES (@id, 1, '{BuildingBlocks.Domain.PublicId.Format("plg", pledgeId)}:charge:1', 'IN_FLIGHT');
            """, pledgeId);
}
