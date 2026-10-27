using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Payments.Application;
using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>Lesson s3-l15: money leaves only after the PSP's report covers the settled campaign and our books match it.</summary>
public sealed class ReconciliationTests(Database database) : IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private WebApplicationFactory<Program> Host()
    {
        var host = database.StartHost();
        _hosts.Add(host);
        return host;
    }

    private static async Task<Result<T>> Send<TCommand, T>(WebApplicationFactory<Program> host, TCommand command)
        where TCommand : ICommand<T>
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<TCommand, T>(command, Ct);
    }

    private async Task<(Scenario Given, Scenario.Published Campaign)> Settled(WebApplicationFactory<Program> host, int pledges)
    {
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        for (var i = 0; i < pledges; i++)
        {
            var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        }

        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await Send<CloseDueCampaigns, int>(host, new CloseDueCampaigns(campaign.CampaignId));
        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) == 1
            && await given.Scalar<long>("SELECT count(*) FROM payments.campaign_payees WHERE campaign_id = @id", campaign.CampaignId) == 1
            // The charge requests come through the broker as messages of their own, not with the decision (s4-l02).
            && await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId) == pledges);
        await Send<RunSettlement, bool>(host, new RunSettlement("reconciliation-test", campaign.CampaignId));
        return (given, campaign);
    }

    private static Task<string> Status(Scenario given, Scenario.Published campaign) =>
        given.Scalar<string>("SELECT status FROM payments.payouts WHERE campaign_id = @id", campaign.CampaignId);

    private static Task<string> FirstPayment(Scenario given, Scenario.Published campaign) =>
        given.Scalar<string>("SELECT psp_payment_id FROM payments.charges WHERE campaign_id = @id ORDER BY pledge_id LIMIT 1", campaign.CampaignId);

    [Fact]
    public async Task A_campaign_is_not_reconciled_or_paid_before_the_report_covers_it()
    {
        var host = Host();
        var (given, campaign) = await Settled(host, 1);
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
        psp.ReportLagsBy(TimeSpan.FromHours(1));
        var transfers = psp.TransfersMade;

        var pass = (await Send<RunMoneyPass, MoneyPass>(host, new RunMoneyPass(campaign.CampaignId))).Value;
        var payout = await Send<PayOut, PayoutDone>(host, new PayOut(campaign.CampaignId));

        Assert.Equal(new MoneyPass(0, 0, 0), pass);
        Assert.Equal("SETTLED", await Status(given, campaign));
        Assert.Equal("not_payable", payout.Errors[0].Code);
        Assert.Equal(transfers, psp.TransfersMade);
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM payments.reconciliation_differences WHERE campaign_id = @id", campaign.CampaignId));

        // Once the report has caught up, the same campaign is reconciled and paid in one pass.
        psp.ReportLagsBy(TimeSpan.Zero);
        Assert.Equal(new MoneyPass(1, 0, 1), (await Send<RunMoneyPass, MoneyPass>(host, new RunMoneyPass(campaign.CampaignId))).Value);
        Assert.Equal("PAID_OUT", await Status(given, campaign));
    }

    [Fact]
    public async Task Books_that_match_the_report_are_reconciled_and_paid_out_in_one_pass()
    {
        var host = Host();
        var (given, campaign) = await Settled(host, 2);
        var pledge = await given.Scalar<Guid>("SELECT pledge_id FROM payments.charges WHERE campaign_id = @id ORDER BY pledge_id LIMIT 1", campaign.CampaignId);
        Assert.True((await Send<RefundPledge, RefundDone>(host, new RefundPledge(pledge))).IsSuccess);

        var pass = (await Send<RunMoneyPass, MoneyPass>(host, new RunMoneyPass(campaign.CampaignId))).Value;

        Assert.Equal(new MoneyPass(1, 0, 1), pass);
        Assert.Equal("PAID_OUT", await Status(given, campaign));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM payments.reconciliation_differences WHERE campaign_id = @id", campaign.CampaignId));
        Assert.True(await given.Scalar<bool>("SELECT reconciled_at IS NOT NULL FROM payments.payouts WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_fee_that_differs_from_the_report_stops_the_payout_and_raises_the_alarm()
    {
        var host = Host();
        var (given, campaign) = await Settled(host, 1);
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
        psp.ReportFeeAs(await FirstPayment(given, campaign), 999);
        var transfers = psp.TransfersMade;
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PaymentsMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref counted, value));
        listener.Start();

        var first = (await Send<RunMoneyPass, MoneyPass>(host, new RunMoneyPass(campaign.CampaignId))).Value;
        var second = (await Send<RunMoneyPass, MoneyPass>(host, new RunMoneyPass(campaign.CampaignId))).Value;

        Assert.Equal(new MoneyPass(0, 1, 0), first);
        Assert.Equal(new MoneyPass(0, 1, 0), second);
        Assert.True(Interlocked.Read(ref counted) >= 2);
        Assert.Equal("SETTLED", await Status(given, campaign));
        Assert.Equal("FEE_DIFFERS", await given.Scalar<string>("SELECT kind FROM payments.reconciliation_differences WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.reconciliation_differences WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(transfers, psp.TransfersMade);
        Assert.Equal("not_payable", (await Send<PayOut, PayoutDone>(host, new PayOut(campaign.CampaignId))).Errors[0].Code);
    }

    [Fact]
    public async Task A_movement_our_books_never_recorded_stops_the_payout()
    {
        var host = Host();
        var (given, campaign) = await Settled(host, 1);
        var pledge = await given.Scalar<Guid>("SELECT pledge_id FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId);
        host.Services.GetRequiredService<FakePaymentProvider>().AddToReport("pi_unbooked", PublicId.Format("plg", pledge), 6_000, 204);

        var pass = (await Send<RunMoneyPass, MoneyPass>(host, new RunMoneyPass(campaign.CampaignId))).Value;

        Assert.Equal(1, pass.Differing);
        Assert.Equal("SETTLED", await Status(given, campaign));
        Assert.Equal("MISSING_IN_BOOKS", await given.Scalar<string>("SELECT kind FROM payments.reconciliation_differences WHERE campaign_id = @id", campaign.CampaignId));
    }
}
