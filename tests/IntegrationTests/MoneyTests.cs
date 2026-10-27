using System.Net;
using System.Net.Http.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Payments.Application;
using Payments.Contracts;
using Payments.Domain;
using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>Lesson s3-a07, built: every cent in an account, every journal balanced, history never rewritten.</summary>
public sealed class MoneyTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private FakePaymentProvider Psp => _host.Services.GetRequiredService<FakePaymentProvider>();

    private async Task<Result<T>> Send<TCommand, T>(TCommand command)
        where TCommand : ICommand<T>
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<TCommand, T>(command, Ct);
    }

    private async Task<IReadOnlyDictionary<string, long>> Balances(Guid campaignId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query<GetCampaignMoney, CampaignMoney>(new GetCampaignMoney(campaignId), Ct)).Value.Balances;
    }

    /// <summary>Posts journals in one transaction of the Payments module, as its handlers do.</summary>
    private async Task<IReadOnlyList<bool>> Post(params MoneyJournal[] journals)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IMoneyLedger>();
        var transaction = scope.ServiceProvider.GetRequiredKeyedService<ITransactionScope>(PaymentsModuleName.Value);
        var posted = new List<bool>();
        await transaction.Run(async ct =>
        {
            foreach (var journal in journals)
            {
                posted.Add(await ledger.Post(journal, ct));
            }

            return Result.Success(true);
        }, Ct);
        return posted;
    }

    /// <summary>A funded campaign of $60 pledges on cards that succeed, settled.</summary>
    private async Task<(Scenario Given, Scenario.Published Campaign)> Settled(int pledges)
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(1_000);
        for (var i = 0; i < pledges; i++)
        {
            var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        }

        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaign.CampaignId));
        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) == 1
            && await given.Scalar<long>("SELECT count(*) FROM payments.campaign_payees WHERE campaign_id = @id", campaign.CampaignId) == 1);
        Assert.True((await Send<RunSettlement, bool>(new RunSettlement("money-test", campaign.CampaignId))).Value);
        Assert.Equal("SETTLED", await given.Scalar<string>("SELECT status FROM payments.payouts WHERE campaign_id = @id", campaign.CampaignId));
        return (given, campaign);
    }

    /// <summary>Reconciles and pays the campaign out, then reads the payout.</summary>
    private async Task<PayoutDone> PaidOut(Guid campaignId)
    {
        Assert.Equal(1, (await Send<RunMoneyPass, MoneyPass>(new RunMoneyPass(campaignId))).Value.PaidOut);
        return (await Send<PayOut, PayoutDone>(new PayOut(campaignId))).Value;
    }

    private static Task<Guid> PledgeOf(Scenario given, Scenario.Published campaign) =>
        given.Scalar<Guid>("SELECT pledge_id FROM payments.charges WHERE campaign_id = @id ORDER BY pledge_id LIMIT 1", campaign.CampaignId);

    [Fact]
    public async Task Each_charge_is_booked_with_its_fee_and_the_creator_is_paid_once()
    {
        var (given, campaign) = await Settled(2);
        Assert.Equal(2L, await given.Scalar<long>("SELECT count(*) FROM payments.money_journals WHERE campaign_id = @id AND kind = 'CHARGE'", campaign.CampaignId));
        var transfers = Psp.TransfersMade;

        var paid = await PaidOut(campaign.CampaignId);
        var again = (await Send<PayOut, PayoutDone>(new PayOut(campaign.CampaignId))).Value;

        // $120 collected, $4.08 PSP fees, a 5% platform fee of $6.00, $109.92 for the creator,
        // of which 10% ($10.99) is held back and $98.93 is paid.
        Assert.Equal(new PayoutDone(9_893, 1_099, paid.PspTransferId), paid);
        Assert.Equal(paid, again);
        Assert.Equal(transfers + 1, Psp.TransfersMade);
        Assert.Equal("PAID_OUT", await given.Scalar<string>("SELECT status FROM payments.payouts WHERE campaign_id = @id", campaign.CampaignId));
        var balances = await Balances(campaign.CampaignId);
        Assert.Equal(0, balances[Accounts.Clearing]);
        Assert.Equal(0, balances[Accounts.ProcessingFees]);
        Assert.Equal(-600, balances[Accounts.PlatformFee]);
        Assert.Equal(-1_099, balances[Accounts.CreatorReserve]);
        Assert.Equal(0, balances.Values.Sum());
    }

    [Fact]
    public async Task Harbor_Lights_balances_to_the_cent_in_the_database()
    {
        var given = new Scenario(database, _host);
        var campaignId = Guid.NewGuid();
        // Only the ledger is under test: the payout starts reconciled.
        await given.Execute("INSERT INTO payments.payouts (campaign_id, currency, status) VALUES (@id, 'USD', 'RECONCILED')", campaignId);
        await given.Execute("INSERT INTO payments.campaign_payees (campaign_id, creator_id) VALUES (@id, gen_random_uuid())", campaignId);
        await Post([.. Enumerable.Range(0, 1_000).Select(i => MoneyJournal.ForCharge(campaignId, $"pi_hl_{campaignId:N}_{i}", 6_000, 204, "USD"))]);
        await Post(MoneyJournal.ForRefund(campaignId, $"re_hl_{campaignId:N}", 6_000, "USD", afterPayout: false, reserveLeftMinor: 0));

        var paid = (await Send<PayOut, PayoutDone>(new PayOut(campaignId))).Value;

        Assert.Equal(4_941_270, paid.AmountMinor);
        Assert.Equal(549_030, paid.ReserveMinor);
        var balances = await Balances(campaignId);
        Assert.Equal(848_730, balances[Accounts.PspBalance]);
        Assert.Equal(-299_700, balances[Accounts.PlatformFee]);
        Assert.Equal(-549_030, balances[Accounts.CreatorReserve]);
        Assert.Equal(0, balances[Accounts.Clearing]);
        Assert.Equal(0, balances[Accounts.CreatorPayable]);
    }

    [Fact]
    public async Task A_replayed_journal_posts_nothing()
    {
        var campaignId = Guid.NewGuid();
        var charge = MoneyJournal.ForCharge(campaignId, $"pi_replay_{campaignId:N}", 6_000, 204, "USD");

        Assert.Equal([true], await Post(charge));
        Assert.Equal([false], await Post(charge));
        Assert.Equal(-6_000, (await Balances(campaignId))[Accounts.Clearing]);
    }

    [Theory]
    [InlineData("INSERT INTO payments.money_entries (journal_id, account, amount_minor) VALUES ((SELECT journal_id FROM j), 'CLEARING', -100)")]
    [InlineData("SELECT 1")]
    public async Task A_journal_that_does_not_balance_cannot_commit(string entries)
    {
        await using var payments = new NpgsqlConnection(database.Payments);
        await payments.OpenAsync(Ct);
        await using var transaction = await payments.BeginTransactionAsync(Ct);
        await using var write = new NpgsqlCommand($"""
            CREATE TEMP TABLE j ON COMMIT DROP AS
            WITH inserted AS (
                INSERT INTO payments.money_journals (campaign_id, kind, source_ref, currency)
                VALUES (gen_random_uuid(), 'CHARGE', 'pi_' || gen_random_uuid(), 'USD') RETURNING journal_id)
            SELECT journal_id FROM inserted;
            {entries};
            """, payments, transaction);
        await write.ExecuteNonQueryAsync(Ct);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync(Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [Theory]
    [InlineData("UPDATE payments.money_entries SET amount_minor = amount_minor + 1")]
    [InlineData("DELETE FROM payments.money_entries")]
    [InlineData("TRUNCATE payments.money_entries")]
    [InlineData("UPDATE payments.money_journals SET kind = 'REFUND'")]
    [InlineData("DELETE FROM payments.money_journals")]
    public async Task The_payments_role_can_add_to_the_money_ledger_but_never_change_it(string change)
    {
        var campaignId = Guid.NewGuid();
        Assert.Equal([true], await Post(MoneyJournal.ForCharge(campaignId, $"pi_rights_{campaignId:N}", 6_000, 204, "USD")));

        await using var payments = new NpgsqlConnection(database.Payments);
        await payments.OpenAsync(Ct);
        await using var write = new NpgsqlCommand(change, payments);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task A_refund_before_payout_comes_out_of_clearing_and_is_made_once()
    {
        var (given, campaign) = await Settled(2);
        var pledge = await PledgeOf(given, campaign);
        var refunds = Psp.RefundsMade;

        var first = (await Send<RefundPledge, RefundDone>(new RefundPledge(pledge))).Value;
        var again = (await Send<RefundPledge, RefundDone>(new RefundPledge(pledge))).Value;

        Assert.Equal(first, again);
        Assert.Equal(refunds + 1, Psp.RefundsMade);
        Assert.Equal("REFUNDED", await given.Scalar<string>("SELECT status FROM payments.charges WHERE pledge_id = @id", pledge));
        var balances = await Balances(campaign.CampaignId);
        Assert.Equal(-6_000, balances[Accounts.Clearing]); // $120 collected, $60 given back
        Assert.Equal(408, balances[Accounts.ProcessingFees]); // the PSP keeps its fee on a refund
    }

    [Fact]
    public async Task A_refund_after_payout_comes_from_the_reserve_and_then_the_creator()
    {
        var (given, campaign) = await Settled(2);
        await PaidOut(campaign.CampaignId);

        await Send<RefundPledge, RefundDone>(new RefundPledge(await PledgeOf(given, campaign)));

        var balances = await Balances(campaign.CampaignId);
        Assert.Equal(0, balances[Accounts.CreatorReserve]); // the whole $10.99 reserve
        Assert.Equal(4_901, balances[Accounts.CreatorPayable]); // and $49.01 the creator now owes
        Assert.Equal(0, balances.Values.Sum());
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.money_journals WHERE campaign_id = @id AND kind = 'REFUND'", campaign.CampaignId));
    }

    [Fact]
    public async Task A_charge_made_before_the_money_ledger_is_booked_before_reconciliation()
    {
        var (given, campaign) = await Settled(2);
        // As if the first charge had been made before this lesson's migration: no CHARGE journal.
        await given.Execute("""
            WITH old AS (
                SELECT j.journal_id FROM payments.money_journals j
                JOIN payments.charges c ON c.psp_payment_id = j.source_ref
                WHERE j.campaign_id = @id AND j.kind = 'CHARGE' ORDER BY c.pledge_id LIMIT 1)
            , gone AS (DELETE FROM payments.money_entries WHERE journal_id IN (SELECT journal_id FROM old))
            DELETE FROM payments.money_journals WHERE journal_id IN (SELECT journal_id FROM old)
            """, campaign.CampaignId);
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.money_journals WHERE campaign_id = @id AND kind = 'CHARGE'", campaign.CampaignId));

        var paid = await PaidOut(campaign.CampaignId);

        // The PSP's fee for the old charge is asked for and booked, so the books match the PSP's
        // report, nothing is recorded as missing, and the payout is the same as ever.
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM payments.reconciliation_differences WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(2L, await given.Scalar<long>("SELECT count(*) FROM payments.money_journals WHERE campaign_id = @id AND kind = 'CHARGE'", campaign.CampaignId));
        Assert.Equal(9_893, paid.AmountMinor);
        Assert.Equal(1_099, paid.ReserveMinor);
    }

    [Fact]
    public async Task A_refund_and_a_payout_wait_for_the_payout_row()
    {
        var (given, campaign) = await Settled(2);
        var pledge = await PledgeOf(given, campaign);
        await given.Execute("UPDATE payments.payouts SET status = 'RECONCILED' WHERE campaign_id = @id", campaign.CampaignId);
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var holding = await owner.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM payments.payouts WHERE campaign_id = @id FOR UPDATE", owner, holding))
        {
            lockRow.Parameters.AddWithValue("id", campaign.CampaignId);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var refund = Send<RefundPledge, RefundDone>(new RefundPledge(pledge));
        var payout = Send<PayOut, PayoutDone>(new PayOut(campaign.CampaignId));
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);

        // Neither can decide "before or after the split" while someone else holds the row.
        Assert.False(refund.IsCompleted);
        Assert.False(payout.IsCompleted);
        await holding.CommitAsync(Ct);
        Assert.True((await refund).IsSuccess);
        Assert.True((await payout).IsSuccess);
        Assert.Equal(0, (await Balances(campaign.CampaignId)).Values.Sum());
    }

    [Fact]
    public async Task A_campaign_that_has_not_settled_cannot_be_paid_out()
    {
        var refused = await Send<PayOut, PayoutDone>(new PayOut(Guid.NewGuid()));

        Assert.Equal("not_settled", refused.Errors[0].Code);
    }
}
