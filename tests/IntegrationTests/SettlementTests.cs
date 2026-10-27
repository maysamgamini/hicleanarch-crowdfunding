using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Payments.Application;
using Payments.Infrastructure;
using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>Lesson 8, built: every pledge of a funded campaign is charged at most once, whatever fails.</summary>
public sealed class SettlementTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private FakePaymentProvider Psp => _host.Payments().Services.GetRequiredService<FakePaymentProvider>();

    private async Task<T> Send<TCommand, T>(TCommand command)
        where TCommand : ICommand<T>
    {
        await using var scope = _host.ServicesFor<TCommand>().CreateAsyncScope();
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
        await Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaign.CampaignId));
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
        Assert.True(await given.Scalar<bool>("SELECT next_attempt_at > now() FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId)); // asked about after the back-off

        await given.Execute("UPDATE payments.charges SET next_attempt_at = now() WHERE campaign_id = @id", campaign.CampaignId); // the back-off has passed
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

        // Worker A claimed both charges, started attempt 1 of one pledge and died before calling the PSP;
        // for the other, it called the PSP and died before recording the answer.
        await given.Execute("UPDATE payments.charges SET claimed_by = 'dead-worker', claimed_until = now() + interval '1 minute' WHERE campaign_id = @id", campaign.CampaignId);
        var pledges = await PledgesOf(given, campaign);
        await StartAttemptBehindTheWorkersBack(given, pledges[0]);
        await StartAttemptBehindTheWorkersBack(given, pledges[1]);
        await Psp.Charge(new ChargeRequest($"cus_unknown", FakePaymentProvider.CardSucceeds, 6_000, "USD", BuildingBlocks.Domain.PublicId.Format("plg", pledges[1]), 1),
            $"{BuildingBlocks.Domain.PublicId.Format("plg", pledges[1])}:charge:1", Ct);

        Assert.False(await Settle(campaign, "worker-b")); // A's leases still hold: B finds nothing to claim
        await given.Execute("UPDATE payments.charges SET claimed_until = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
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
    public async Task A_declined_card_enters_its_recovery_window_and_waits_a_day_for_its_next_attempt()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardDeclined);

        await Settle(campaign);
        await Settle(campaign);

        Assert.Equal(1L, await Count(given, campaign, "PAYMENT_FAILED"));
        Assert.Equal(1, await given.Scalar<int>("SELECT attempt_no FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));
        Assert.True(await given.Scalar<bool>("SELECT next_attempt_at > now() + interval '23 hours' FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal("insufficient_funds", await given.Scalar<string>("SELECT last_decline_code FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));
        Assert.NotEqual("DONE", await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));
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

    [Fact]
    public async Task A_job_that_must_wait_does_not_keep_the_workers_from_the_next_one()
    {
        var (given, waiting) = await Funded(FakePaymentProvider.CardSucceeds);
        var (_, next) = await Funded(FakePaymentProvider.CardSucceeds);

        // The first job's only charge is not due for an hour. Both jobs are made the oldest in the
        // table, so a worker that claims "any job" meets them first, in this order.
        await given.Execute("UPDATE payments.charges SET next_attempt_at = now() + interval '1 hour' WHERE campaign_id = @id", waiting.CampaignId);
        await given.Execute("UPDATE payments.settlement_jobs SET created_at = '2000-01-01' WHERE campaign_id = @id", waiting.CampaignId);
        await given.Execute("UPDATE payments.settlement_jobs SET created_at = '2000-01-02' WHERE campaign_id = @id", next.CampaignId);

        Assert.False(await Send<RunSettlement, bool>(new RunSettlement("any-worker"))); // the waiting job: nothing to do yet
        Assert.True(await given.Scalar<bool>("SELECT not_before > now() + interval '59 minutes' FROM payments.settlement_jobs WHERE campaign_id = @id", waiting.CampaignId));
        Assert.True(await Send<RunSettlement, bool>(new RunSettlement("any-worker")));  // the next job, not the waiting one again

        Assert.Equal(1L, await Count(given, next, "CHARGED"));
        Assert.Equal(1L, await Count(given, waiting, "TO_CHARGE"));
    }

    [Fact]
    public async Task A_charge_webhook_records_an_attempt_whose_reply_was_lost_once()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardSucceeds);
        Psp.LoseNextReplies(1);
        await Settle(campaign);
        Assert.Equal(1L, await Count(given, campaign, "CHARGING"));

        var pledge = (await PledgesOf(given, campaign))[0];
        var publicId = BuildingBlocks.Domain.PublicId.Format("plg", pledge);
        var customer = await given.Scalar<string>(
            "SELECT p.psp_customer_id FROM payments.psp_customers p JOIN payments.charges c ON c.backer_id = p.backer_id WHERE c.pledge_id = @id", pledge);
        var paid = Assert.IsType<ChargeOutcome.Succeeded>(await Psp.LookUp(new ChargeReference(null, customer, publicId, 1), Ct));
        var webhook = Psp.Webhook("charge_succeeded", new { payment_id = paid.PspPaymentId, pledge_id = publicId, attempt_no = 1 });

        Assert.Equal(HttpStatusCode.OK, (await Deliver(webhook)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Deliver(webhook)).StatusCode); // the PSP sends it again

        Assert.Equal(1L, await Count(given, campaign, "CHARGED"));
        Assert.Equal(1L, await given.Scalar<long>(
            "SELECT count(*) FROM payments.outbox WHERE type = 'PledgeChargeSucceeded' AND payload ->> 'pledgeId' = @id::text", pledge));
        await Settle(campaign); // the resolver now finds nothing to resolve
        Assert.Equal(1L, await given.Scalar<long>(
            "SELECT count(*) FROM payments.outbox WHERE type = 'PledgeChargeSucceeded' AND payload ->> 'pledgeId' = @id::text", pledge));
    }

    [Fact]
    public async Task A_stopping_worker_gives_its_charges_back_at_once()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardSucceeds);
        var worker = _host.Payments().Services.GetRequiredService<SettlementWorker>();
        await given.Execute("UPDATE payments.settlement_jobs SET status = 'RUNNING' WHERE campaign_id = @id", campaign.CampaignId);
        await given.Execute(
            $"UPDATE payments.charges SET claimed_by = '{worker.WorkerName}', claimed_until = now() + interval '1 minute' WHERE campaign_id = @id",
            campaign.CampaignId);

        await worker.Stop(Ct);

        Assert.Equal("PENDING", await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));
        Assert.True(await Settle(campaign, "another-task")); // no wait for the lease
        Assert.Equal(1L, await Count(given, campaign, "CHARGED"));
    }

    [Fact]
    public async Task Two_workers_that_reach_the_same_charge_at_once_start_one_attempt_and_charge_once()
    {
        var (given, campaign) = await Funded(FakePaymentProvider.CardSucceeds);
        var pledge = (await PledgesOf(given, campaign))[0];
        var publicId = BuildingBlocks.Domain.PublicId.Format("plg", pledge);
        var before = Psp.ChargesMade;

        // Both find the charge TO_CHARGE (a lease ran out while the first was slow, say) and start an
        // attempt at the same moment: a lock held on the row lets them go together when it is released.
        await using var gate = new Npgsql.NpgsqlConnection(given.OwnerConnection);
        await gate.OpenAsync(Ct);
        await using var hold = await gate.BeginTransactionAsync(Ct);
        await using (var lockRow = new Npgsql.NpgsqlCommand("SELECT 1 FROM payments.charges WHERE pledge_id = @id FOR UPDATE", gate, hold))
        {
            lockRow.Parameters.AddWithValue("id", pledge);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        async Task<int?> Start()
        {
            await using var scope = _host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISettlements>().TryStartAttempt(pledge, publicId, onSession: false, lookUpAt: null, Ct);
        }

        var first = Task.Run(Start, Ct);
        var second = Task.Run(Start, Ct);
        await Scenario.Eventually(async () => await given.Scalar<long>(
            "SELECT count(DISTINCT pid) FROM pg_locks l JOIN pg_stat_activity s USING (pid) WHERE NOT l.granted AND s.usename = 'payments_app' AND s.query LIKE '%attempt_no + 1%'", pledge) >= 2);
        await hold.CommitAsync(Ct);
        int?[] started = [await first, await second];

        Assert.Equal([1], started.Where(n => n is not null).Cast<int>());
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.charge_attempts WHERE pledge_id = @id", pledge));

        // The one attempt is then settled with its own key: one charge at the PSP.
        while (await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) != "DONE")
        {
            await Settle(campaign);
        }

        Assert.Equal(before + 1, Psp.ChargesMade);
        Assert.Equal(1L, await Count(given, campaign, "CHARGED"));
    }
    private Task<HttpResponseMessage> Deliver(SignedWebhook webhook)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/psp") { Content = new StringContent(webhook.Body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.Header, webhook.Signature);
        return Database.Client(_host).SendAsync(request, Ct);
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
