using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;
using Payments.Domain;

using Pledges.Contracts;

namespace Payments.Application;

/// <summary>A charge the worker is about to act on, with the backer's customer at the PSP.</summary>
public sealed record ChargeWork(Guid PledgeId, Guid BackerId, long AmountMinor, string Currency, string PaymentMethodRef, ChargeStatus Status, int AttemptNo, string? PspPaymentId)
{
    public string PledgePublicId => PublicId.Format("plg", PledgeId);
}

public sealed record SettlementCounts(int Expected, int Charged, int Failed, int Open);

/// <summary>Charges, attempts and settlement jobs, each move conditional on where the row stands.</summary>
public interface ISettlements
{
    Task CreateCharge(Charge charge, CancellationToken cancellationToken);

    Task CreateJob(Guid campaignId, int expectedCharges, CancellationToken cancellationToken);

    /// <summary>
    /// Claims one job nobody holds, or whose lease ran out: <c>FOR UPDATE SKIP LOCKED</c>. With
    /// <paramref name="campaignId"/>, only that campaign's job. Null: nothing to do.
    /// </summary>
    Task<Guid?> Claim(string worker, TimeSpan lease, Guid? campaignId, CancellationToken cancellationToken);

    /// <summary>Extends this worker's lease. False: the lease was lost to another worker.</summary>
    Task<bool> Renew(Guid campaignId, string worker, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Gives the job back for a later pass, if this worker still holds it.</summary>
    Task Release(Guid campaignId, string worker, CancellationToken cancellationToken);

    /// <summary>RUNNING to DONE, if this worker still holds it. False: it does not.</summary>
    Task<bool> TryFinish(Guid campaignId, string worker, CancellationToken cancellationToken);

    /// <summary>The campaign's charges that need work now: CHARGING ones to resolve, TO_CHARGE ones whose time has come.</summary>
    Task<IReadOnlyList<ChargeWork>> Workable(Guid campaignId, CancellationToken cancellationToken);

    Task<SettlementCounts> Counts(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// TO_CHARGE to CHARGING with the next attempt number, and the attempt row IN_FLIGHT with its key.
    /// Committed before the PSP is called. Null: another worker has it.
    /// </summary>
    Task<int?> TryStartAttempt(Guid pledgeId, string pledgePublicId, CancellationToken cancellationToken);

    Task<bool> TryRecordSuccess(Guid pledgeId, int attemptNo, string pspPaymentId, CancellationToken cancellationToken);

    /// <summary>Records a decline. With attempts left, the charge goes back to TO_CHARGE for later; otherwise it is FAILED.</summary>
    Task<bool> TryRecordDecline(Guid pledgeId, int attemptNo, string code, string? pspPaymentId, bool final, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken);

    Task<string> CustomerOf(Guid backerId, CancellationToken cancellationToken);
}

/// <summary>Configuration section <c>Payments:Settlement</c>.</summary>
public sealed class SettlementOptions
{
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A declined charge is tried again after this; three attempts span 24 hours.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromHours(12);

    public int MaxAttempts { get; set; } = 3;
}

/// <summary>A pledge of a funded campaign gets its charge, once, whatever repeats the event.</summary>
public sealed class CreateCharge(ISettlements settlements) : IIntegrationEventHandler<PledgeChargeRequested>
{
    public Task Handle(PledgeChargeRequested requested, CancellationToken cancellationToken) =>
        settlements.CreateCharge(
            Charge.For(requested.PledgeId, requested.CampaignId, requested.BackerId, requested.AmountMinor, requested.Currency, requested.PaymentMethodRef),
            cancellationToken);
}

/// <summary>A funded campaign gets one settlement job. An unsuccessful one needs none: nothing was charged.</summary>
public sealed class CreateSettlementJob(ISettlements settlements) : IIntegrationEventHandler<CampaignFundingDecided>
{
    public Task Handle(CampaignFundingDecided decided, CancellationToken cancellationToken) =>
        decided.Outcome == CampaignFundingDecided.Funded
            ? settlements.CreateJob(decided.CampaignId, decided.Backers, cancellationToken)
            : Task.CompletedTask;
}

/// <summary>
/// One pass of a settlement worker: claim a job, work its charges, finish it or give it back. With
/// <see cref="CampaignId"/>, only that campaign's job (an operator's or a test's pass).
/// </summary>
public sealed record RunSettlement(string Worker, Guid? CampaignId = null) : ICommand<bool>;

/// <summary>
/// The orchestrator. The lease only decides who works; what is true is decided by the conditional
/// moves and the PSP's idempotency keys. Every attempt is committed before the PSP is called. A
/// timeout leaves it IN_FLIGHT, and the next pass asks the PSP about that same attempt before
/// anything else: a new attempt, with a new key, is made only after the last one is known to have failed.
/// </summary>
public sealed class RunSettlementHandler(
    ISettlements settlements,
    IPaymentProvider psp,
    SettlementOptions options,
    TimeProvider clock,
    [FromKeyedServices(PaymentsModuleName.Value)] IOutbox outbox,
    [FromKeyedServices(PaymentsModuleName.Value)] ITransactionScope transaction) : ICommandHandler<RunSettlement, bool>
{
    public async Task<Result<bool>> Handle(RunSettlement command, CancellationToken cancellationToken)
    {
        var settings = options;
        var campaignId = await settlements.Claim(command.Worker, settings.Lease, command.CampaignId, cancellationToken);
        if (campaignId is not { } id)
        {
            return Result.Success(false);
        }

        foreach (var charge in await settlements.Workable(id, cancellationToken))
        {
            if (!await settlements.Renew(id, command.Worker, settings.Lease, cancellationToken))
            {
                return Result.Success(true); // the lease went to another worker; it carries on from the charges' statuses
            }

            var customer = await settlements.CustomerOf(charge.BackerId, cancellationToken);
            if (charge.Status == ChargeStatus.Charging)
            {
                await Resolve(id, charge, customer, cancellationToken);
            }
            else
            {
                await ChargeOnce(id, charge, customer, cancellationToken);
            }
        }

        var counts = await settlements.Counts(id, cancellationToken);
        if (counts.Open == 0 && counts.Charged + counts.Failed >= counts.Expected)
        {
            await transaction.Run(async ct =>
            {
                if (!await settlements.TryFinish(id, command.Worker, ct))
                {
                    return Result.Success(false);
                }

                await outbox.Add(new CampaignSettled(Guid.CreateVersion7(), clock.GetUtcNow(), id, counts.Charged, counts.Failed), ct);
                return Result.Success(true);
            }, cancellationToken);
        }
        else
        {
            await settlements.Release(id, command.Worker, cancellationToken);
        }

        return Result.Success(true);
    }

    private async Task ChargeOnce(Guid campaignId, ChargeWork charge, string customer, CancellationToken ct)
    {
        var attemptNo = await settlements.TryStartAttempt(charge.PledgeId, charge.PledgePublicId, ct);
        if (attemptNo is not { } n)
        {
            return;
        }

        var outcome = await psp.Charge(
            new ChargeRequest(customer, charge.PaymentMethodRef, charge.AmountMinor, charge.Currency, charge.PledgePublicId, n),
            Charge.KeyFor(charge.PledgePublicId, n),
            ct);
        await Record(campaignId, charge, n, outcome, ct);
    }

    private async Task Resolve(Guid campaignId, ChargeWork charge, string customer, CancellationToken ct)
    {
        var n = charge.AttemptNo;
        var outcome = await psp.LookUp(new ChargeReference(charge.PspPaymentId, customer, charge.PledgePublicId, n), ct);
        if (outcome is ChargeOutcome.NotFound)
        {
            // The first request never reached the PSP: send that same attempt again, with its own key.
            outcome = await psp.Charge(
                new ChargeRequest(customer, charge.PaymentMethodRef, charge.AmountMinor, charge.Currency, charge.PledgePublicId, n),
                Charge.KeyFor(charge.PledgePublicId, n),
                ct);
        }

        await Record(campaignId, charge, n, outcome, ct);
    }

    private Task Record(Guid campaignId, ChargeWork charge, int n, ChargeOutcome outcome, CancellationToken cancellationToken) =>
        outcome switch
        {
            ChargeOutcome.Succeeded s => transaction.Run(async ct =>
            {
                if (await settlements.TryRecordSuccess(charge.PledgeId, n, s.PspPaymentId, ct))
                {
                    await outbox.Add(new PledgeChargeSucceeded(Guid.CreateVersion7(), clock.GetUtcNow(), charge.PledgeId, campaignId, n, s.PspPaymentId, charge.AmountMinor), ct);
                }

                return Result.Success(true);
            }, cancellationToken),
            ChargeOutcome.Declined d => transaction.Run(async ct =>
            {
                var final = n >= options.MaxAttempts;
                var next = final ? (DateTimeOffset?)null : clock.GetUtcNow() + options.RetryAfter;
                if (await settlements.TryRecordDecline(charge.PledgeId, n, d.Code, d.PspPaymentId, final, next, ct))
                {
                    await outbox.Add(new PledgeChargeFailed(Guid.CreateVersion7(), clock.GetUtcNow(), charge.PledgeId, campaignId, n, d.Code, final), ct);
                }

                return Result.Success(true);
            }, cancellationToken),
            _ => Task.CompletedTask, // Unknown: the attempt stays IN_FLIGHT, and the next pass asks about it
        };
}
