using System.Diagnostics.Metrics;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Payments.Domain;

namespace Payments.Application;

/// <summary>What reconciliation reads from our side, and where it records what it found.</summary>
public interface IReconciliation
{
    /// <summary>The campaign's charges and refunds as our ledger booked them, by the PSP's id for each.</summary>
    Task<IReadOnlyList<MoneyMovement>> Booked(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>The public ids of the campaign's charged pledges, which the PSP's report carries in its metadata.</summary>
    Task<IReadOnlySet<string>> PledgesOf(Guid campaignId, CancellationToken cancellationToken);

    Task<DateTimeOffset?> FirstChargeAt(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>When the campaign's last charge or refund was booked; the PSP's report must reach it.</summary>
    Task<DateTimeOffset?> LastMovementAt(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Keeps each difference found, once per movement and kind, until someone explains it.</summary>
    Task Record(Guid campaignId, IReadOnlyList<MoneyDifference> differences, CancellationToken cancellationToken);
}

/// <summary>The moves before a payout, each conditional on where the payout stands.</summary>
public interface IPayoutSteps
{
    Task<IReadOnlyList<Guid>> InStatus(PayoutStatus status, Guid? campaignId, int limit, CancellationToken cancellationToken);

    Task<bool> TryMove(Guid campaignId, PayoutStatus from, PayoutStatus to, CancellationToken cancellationToken);
}

/// <summary>The meter finance's alarm watches: any difference between our books and the PSP's.</summary>
public static class PaymentsMetrics
{
    public const string MeterName = "CrowdFunding.Payments";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> ReconciliationDifferences =
        Meter.CreateCounter<long>("payments.reconciliation.differences", description: "Differences between our money ledger and the PSP's report; any is an alarm.");
}

/// <summary>
/// One pass over the money: reconcile settled campaigns against the PSP's report, and pay out the
/// reconciled ones. With <see cref="CampaignId"/>, only that campaign (an operator's or a test's pass).
/// </summary>
public sealed record RunMoneyPass(Guid? CampaignId = null) : ICommand<MoneyPass>;

public sealed record MoneyPass(int Reconciled, int Differing, int PaidOut);

/// <summary>
/// A settled campaign has no charge that can still change: CampaignSettled is published only once
/// every charge is final, dunning windows included, so there is no second wait here. What the pass
/// waits for is the PSP's report: a campaign is compared once the report covers its last movement.
/// A difference is never resolved here: it is recorded, counted for the alarm, and the campaign
/// stays SETTLED, so no money leaves until a person has explained it. Every move is conditional,
/// so two tasks running the pass at once do each step once.
/// </summary>
public sealed class RunMoneyPassHandler(
    IPayoutSteps steps,
    IReconciliation reconciliation,
    IPayouts payouts,
    IPaymentProvider psp,
    ChargeBookkeeping bookkeeping,
    IDispatcher dispatcher,
    TimeProvider clock) : ICommandHandler<RunMoneyPass, MoneyPass>
{
    private const int Batch = 100;

    public async Task<Result<MoneyPass>> Handle(RunMoneyPass command, CancellationToken cancellationToken)
    {
        int reconciled = 0, differing = 0, paid = 0;
        foreach (var id in await steps.InStatus(PayoutStatus.Settled, command.CampaignId, Batch, cancellationToken))
        {
            // Charges made before the money ledger existed are booked first, with the fee the PSP
            // reports, or every one of them would show as missing from our books.
            var booked = await bookkeeping.EnsureBooked(id, cancellationToken);
            if (!booked.IsSuccess)
            {
                if (booked.Errors[0] == PaymentErrors.ChargeNotBooked)
                {
                    PaymentsMetrics.ReconciliationDifferences.Add(1);
                    differing++;
                }

                continue; // the PSP did not answer, or a charge needs a person: the campaign stays SETTLED
            }

            var differences = await Compare(id, cancellationToken);
            if (differences is null)
            {
                continue; // the report does not reach the campaign's last movement yet
            }

            if (differences.Count == 0)
            {
                reconciled += await steps.TryMove(id, PayoutStatus.Settled, PayoutStatus.Reconciled, cancellationToken) ? 1 : 0;
            }
            else
            {
                await reconciliation.Record(id, differences, cancellationToken);
                PaymentsMetrics.ReconciliationDifferences.Add(differences.Count);
                differing++;
            }
        }

        foreach (var id in await steps.InStatus(PayoutStatus.Reconciled, command.CampaignId, Batch, cancellationToken))
        {
            paid += (await dispatcher.Send<PayOut, PayoutDone>(new PayOut(id), cancellationToken)).IsSuccess ? 1 : 0;
        }

        return Result.Success(new MoneyPass(reconciled, differing, paid));
    }

    /// <summary>The differences, or null while the PSP's report does not yet cover the campaign.</summary>
    private async Task<IReadOnlyList<MoneyDifference>?> Compare(Guid campaignId, CancellationToken ct)
    {
        var settledAt = (await payouts.Find(campaignId, ct))!.SettledAt;
        var last = await reconciliation.LastMovementAt(campaignId, ct) is { } moved && moved > settledAt ? moved : settledAt;
        var booked = await reconciliation.Booked(campaignId, ct);
        var pledges = await reconciliation.PledgesOf(campaignId, ct);
        var from = (await reconciliation.FirstChargeAt(campaignId, ct) ?? clock.GetUtcNow()) - TimeSpan.FromDays(1);
        // Asked through the last movement at least, the report says itself how far it is complete.
        var now = clock.GetUtcNow();
        var report = await psp.BalanceTransactions(from, last > now ? last : now, ct);
        if (report.CoveredUntil < last)
        {
            return null;
        }

        var reported = report.Transactions
            .Where(t => pledges.Contains(t.PledgeId))
            .Select(t => new MoneyMovement(t.SourceId, t.AmountMinor, t.FeeMinor))
            .ToList();
        return MoneyDifference.Between(booked, reported);
    }
}
