using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;
using Payments.Domain;

using Pledges.Contracts;

namespace Payments.Application;

/// <summary>
/// A charge the worker or a backer is about to act on. <see cref="PaymentFailedAt"/> opened its
/// recovery window; <see cref="OnSession"/>: its latest attempt waits for the backer, not the PSP.
/// </summary>
public sealed record ChargeWork(
    Guid PledgeId,
    Guid BackerId,
    long AmountMinor,
    string Currency,
    string PaymentMethodRef,
    ChargeStatus Status,
    int AttemptNo,
    string? PspPaymentId,
    Guid CampaignId = default,
    DateTimeOffset? PaymentFailedAt = null,
    string? LastDeclineCode = null,
    bool OnSession = false,
    string? LastAdviceCode = null)
{
    public string PledgePublicId => PublicId.Format("plg", PledgeId);
}

public sealed record SettlementCounts(int Expected, int Charged, int Failed, int Open);

/// <summary>A charge as the webhook handler finds it by its pledge: the work, and its campaign.</summary>
public sealed record ChargeOfPledge(Guid CampaignId, ChargeWork Work);

/// <summary>How far a campaign's settlement has got, and when it should be done at the charges' rate.</summary>
public sealed record SettlementProgress(string Status, int Expected, int Charged, int Failed, int Open, int EtaSeconds);

/// <summary>Charges, attempts and settlement jobs, each move conditional on where the row stands.</summary>
public interface ISettlements
{
    Task CreateCharge(Charge charge, CancellationToken cancellationToken);

    Task CreateJob(Guid campaignId, int expectedCharges, CancellationToken cancellationToken);

    /// <summary>
    /// Claims one job nobody holds, or whose lease ran out: <c>FOR UPDATE SKIP LOCKED</c>, skipping jobs
    /// whose <c>not_before</c> is still ahead, the job whose last turn was longest ago first, so
    /// campaigns take turns. With <paramref name="campaignId"/>, only that campaign's job, whatever its
    /// <c>not_before</c>. Null: nothing to do.
    /// </summary>
    Task<Guid?> Claim(string worker, TimeSpan lease, Guid? campaignId, IReadOnlyCollection<Guid>? among, CancellationToken cancellationToken);

    /// <summary>Extends this worker's lease. False: the lease was lost to another worker.</summary>
    Task<bool> Renew(Guid campaignId, string worker, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Gives the job back for a later turn, if this worker still holds it, and records when its turn
    /// ended. If none of its charges can be worked now, no worker claims it again before the earliest
    /// retry is due, or before <paramref name="unknownBackoff"/> when it waits on an unknown attempt or
    /// on charges still to be created; and never before <paramref name="pause"/> has passed.
    /// </summary>
    Task Release(Guid campaignId, string worker, TimeSpan unknownBackoff, TimeSpan pause, CancellationToken cancellationToken);

    /// <summary>Gives back every job this worker holds, at once: the worker is stopping.</summary>
    Task ReleaseAll(string worker, CancellationToken cancellationToken);

    /// <summary>RUNNING to DONE, if this worker still holds it. False: it does not.</summary>
    Task<bool> TryFinish(Guid campaignId, string worker, CancellationToken cancellationToken);

    /// <summary>
    /// The next batch of the campaign's charges that need work now, at most <paramref name="batchSize"/>:
    /// CHARGING ones to resolve first, then TO_CHARGE ones whose time has come.
    /// </summary>
    Task<IReadOnlyList<ChargeWork>> Workable(Guid campaignId, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// The campaign's settlement now, and its open charges against every settling campaign's: with
    /// turns taken in rotation, a campaign is done when each other campaign has had as many charges
    /// as it, up to their own. Null: no settlement for this campaign.
    /// </summary>
    Task<(SettlementCounts Counts, string Status, int OpenAheadOrAlongside)?> Progress(Guid campaignId, CancellationToken cancellationToken);

    Task<SettlementCounts> Counts(Guid campaignId, CancellationToken cancellationToken);

    Task<ChargeOfPledge?> FindCharge(Guid pledgeId, CancellationToken cancellationToken);

    /// <summary>
    /// TO_CHARGE or PAYMENT_FAILED to CHARGING with the next attempt number, and the attempt row
    /// IN_FLIGHT with its key. Committed before the PSP is called. Null: another worker, or the backer, has it.
    /// </summary>
    Task<int?> TryStartAttempt(Guid pledgeId, string pledgePublicId, bool onSession, CancellationToken cancellationToken);

    /// <summary>The PSP's id for an on-session attempt, kept so its webhook and the resolver find it.</summary>
    Task RecordPayment(Guid pledgeId, int attemptNo, string pspPaymentId, CancellationToken cancellationToken);

    Task<bool> TryRecordSuccess(Guid pledgeId, int attemptNo, string pspPaymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a decline: PAYMENT_FAILED until <paramref name="nextAttemptAt"/>, the window opening at
    /// the first decline; or DROPPED.
    /// </summary>
    Task<bool> TryRecordDecline(Guid pledgeId, int attemptNo, string code, string? adviceCode, string? pspPaymentId, bool drop, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken);

    /// <summary>
    /// PAYMENT_FAILED to DROPPED with no new attempt, if its latest attempt is still <paramref name="attemptNo"/>:
    /// the window ended and the backer never acted on a decline that waits for them.
    /// </summary>
    Task<bool> TryDropWaiting(Guid pledgeId, int attemptNo, CancellationToken cancellationToken);

    /// <summary>A backer's own charge. Null: no charge for this pledge and backer.</summary>
    Task<ChargeWork?> FindForBacker(Guid pledgeId, Guid backerId, CancellationToken cancellationToken);

    /// <summary>The charge an on-session payment belongs to, with that attempt's number. Null: not ours, or not waiting.</summary>
    Task<(ChargeWork Charge, int AttemptNo)?> FindByPayment(string pspPaymentId, CancellationToken cancellationToken);

    /// <summary>
    /// A new saved card for a charge in its recovery window, tried at once: the charge is due now,
    /// and so is its settlement job. The last decline was about the old card, so it is forgotten.
    /// False: it is not waiting in PAYMENT_FAILED.
    /// </summary>
    Task<bool> TryUseNewCard(Guid pledgeId, string paymentMethodRef, CancellationToken cancellationToken);

    Task<string> CustomerOf(Guid backerId, CancellationToken cancellationToken);
}

/// <summary>Configuration section <c>Payments:Settlement</c>.</summary>
public sealed class SettlementOptions
{
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A call to the PSP that takes longer is given up as Unknown. Well under the lease, which is
    /// renewed before every call, so a worker never charges on a lease that has run out.
    /// </summary>
    public TimeSpan PspCallTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Charges one turn works through before the job goes back for the next campaign's turn.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>How long a charge whose outcome is unknown waits before the PSP is asked about it again.</summary>
    public TimeSpan UnknownBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a job waits after the PSP could not be reached, or the circuit was open.</summary>
    public TimeSpan PauseWhenPspFails { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Configuration section <c>Payments:Dunning</c>: the recovery window of lesson s3-a04.</summary>
public sealed class DunningOptions
{
    public TimeSpan Window { get; set; } = DunningPolicy.Default.Window;

    public TimeSpan[] SoftRetries { get; set; } = [.. DunningPolicy.Default.SoftRetries];

    public int MaxAttempts { get; set; } = DunningPolicy.Default.MaxAttempts;

    public DunningPolicy Policy => new(Window, SoftRetries, MaxAttempts);
}

/// <summary>
/// What an attempt's outcome does to its charge, wherever the outcome comes from: the worker's own
/// call, the resolver's question, or the PSP's webhook. Every move is conditional on the attempt
/// still being IN_FLIGHT, so the same outcome told twice moves the charge once. Runs inside the
/// caller's transaction, with its event.
/// </summary>
public sealed class ChargeOutcomes(
    ISettlements settlements,
    IMoneyLedger ledger,
    DunningPolicy dunning,
    TimeProvider clock,
    [FromKeyedServices(PaymentsModuleName.Value)] IOutbox outbox)
{
    public async Task<bool> Record(ChargeWork charge, int n, ChargeOutcome outcome, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        switch (outcome)
        {
            case ChargeOutcome.Succeeded s:
                if (!await settlements.TryRecordSuccess(charge.PledgeId, n, s.PspPaymentId, ct))
                {
                    return false;
                }

                // The journal goes in the success's own transaction. Without the fee it waits for ChargeBookkeeping.
                if (s.FeeMinor is { } fee)
                {
                    await ledger.Post(MoneyJournal.ForCharge(charge.CampaignId, s.PspPaymentId, charge.AmountMinor, fee, charge.Currency), ct);
                }

                await outbox.Add(new PledgeChargeSucceeded(Guid.CreateVersion7(), now, charge.PledgeId, charge.CampaignId, n, s.PspPaymentId, charge.AmountMinor, charge.BackerId), ct);
                return true;

            case ChargeOutcome.Declined d:
                var (drop, next) = dunning.AfterDecline(n, d.Code, d.AdviceCode, charge.PaymentFailedAt ?? now, now);
                if (!await settlements.TryRecordDecline(charge.PledgeId, n, d.Code, d.AdviceCode, d.PspPaymentId, drop, next, ct))
                {
                    return false;
                }

                await outbox.Add(new PledgeChargeFailed(Guid.CreateVersion7(), now, charge.PledgeId, charge.CampaignId, n, d.Code, drop, charge.BackerId,
                    !drop && DunningPolicy.RetriesOnItsOwn(d.Code, d.AdviceCode)), ct);
                return true;

            default:
                return false; // Unknown: the attempt stays IN_FLIGHT, and the next pass asks about it
        }
    }

    /// <summary>The window of a decline that waited for the backer has ended with nothing from them: dropped, with no new attempt.</summary>
    public async Task<bool> DropWaiting(ChargeWork charge, CancellationToken ct)
    {
        if (!await settlements.TryDropWaiting(charge.PledgeId, charge.AttemptNo, ct))
        {
            return false;
        }

        await outbox.Add(new PledgeChargeFailed(Guid.CreateVersion7(), clock.GetUtcNow(), charge.PledgeId, charge.CampaignId, charge.AttemptNo,
            charge.LastDeclineCode ?? "", Final: true, charge.BackerId), ct);
        return true;
    }
}

/// <summary>How far a campaign's settlement has got.</summary>
public sealed record GetSettlementProgress(Guid CampaignId) : IQuery<SettlementProgress>;

/// <summary>
/// Progress, and an ETA from the charges' share of the budget: the rate is the PSP's, not ours. An
/// operator's question about work in progress, read from the writer: Payments keeps no read side.
/// </summary>
[ReadsTheWriter]
public sealed class GetSettlementProgressHandler(ISettlements settlements, PspBudget budget) : IQueryHandler<GetSettlementProgress, SettlementProgress>
{
    public async Task<Result<SettlementProgress>> Handle(GetSettlementProgress query, CancellationToken cancellationToken)
    {
        var progress = await settlements.Progress(query.CampaignId, cancellationToken);
        if (progress is not { } p)
        {
            return Result.Failure<SettlementProgress>(PaymentErrors.NoSettlement);
        }

        var eta = (int)Math.Ceiling(p.OpenAheadOrAlongside / budget.ChargesPerSecond);
        return Result.Success(new SettlementProgress(p.Status, p.Counts.Expected, p.Counts.Charged, p.Counts.Failed, p.Counts.Open, eta));
    }
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
/// One pass of a settlement worker: claim a job, work a batch of its charges, finish it or give it
/// back. With <see cref="CampaignId"/>, only that campaign's job, whenever it is due (an operator's
/// or a test's pass). With <see cref="Among"/>, the next due job of those campaigns, in turn. True
/// if the pass did something: worked a charge or finished the job.
/// </summary>
public sealed record RunSettlement(string Worker, Guid? CampaignId = null, IReadOnlyCollection<Guid>? Among = null) : ICommand<bool>;

/// <summary>
/// The orchestrator. The lease only decides who works; what is true is decided by the conditional
/// moves and the PSP's idempotency keys. Every attempt is committed before the PSP is called. A
/// timeout leaves it IN_FLIGHT, and the next pass asks the PSP about that same attempt before
/// anything else: a new attempt, with a new key, is made only after the last one is known to have failed.
/// </summary>
public sealed class RunSettlementHandler(
    ISettlements settlements,
    IPayouts payouts,
    IPaymentProvider psp,
    SettlementOptions options,
    ChargeOutcomes outcomes,
    DunningPolicy dunning,
    TimeProvider clock,
    [FromKeyedServices(PaymentsModuleName.Value)] IOutbox outbox,
    [FromKeyedServices(PaymentsModuleName.Value)] ITransactionScope transaction) : ICommandHandler<RunSettlement, bool>
{
    public async Task<Result<bool>> Handle(RunSettlement command, CancellationToken cancellationToken)
    {
        var settings = options;
        var campaignId = await settlements.Claim(command.Worker, settings.Lease, command.CampaignId, command.Among, cancellationToken);
        if (campaignId is not { } id)
        {
            return Result.Success(false);
        }

        var worked = 0;
        try
        {
            foreach (var charge in await settlements.Workable(id, settings.BatchSize, cancellationToken))
            {
                worked++;
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
        }
        catch (HttpRequestException)
        {
            // The PSP cannot be reached, or the circuit is open: give the job back and pause. A charge
            // left CHARGING is resolved with its own key on the next turn.
            await settlements.Release(id, command.Worker, settings.UnknownBackoff, settings.PauseWhenPspFails, cancellationToken);
            return Result.Success(false);
        }

        var counts = await settlements.Counts(id, cancellationToken);
        if (counts.Open == 0 && counts.Charged + counts.Failed >= counts.Expected)
        {
            var finished = await transaction.Run(async ct =>
            {
                if (!await settlements.TryFinish(id, command.Worker, ct))
                {
                    return Result.Success(false);
                }

                await outbox.Add(new CampaignSettled(Guid.CreateVersion7(), clock.GetUtcNow(), id, counts.Charged, counts.Failed), ct);
                await payouts.Open(id, ct); // the money's way to the creator starts here
                return Result.Success(true);
            }, cancellationToken);
            return Result.Success(worked > 0 || finished.Value);
        }

        await settlements.Release(id, command.Worker, settings.UnknownBackoff, TimeSpan.Zero, cancellationToken);
        return Result.Success(worked > 0);
    }

    private async Task ChargeOnce(Guid campaignId, ChargeWork charge, string customer, CancellationToken ct)
    {
        // A decline that waits for the backer is due again only when its window ends. With no new card
        // (which forgets the decline) and no confirmation, the card is not tried again: the issuer said
        // it will not pass, and the card networks count every retry against the merchant.
        if (charge.Status == ChargeStatus.PaymentFailed && charge.LastDeclineCode is { } code && !DunningPolicy.RetriesOnItsOwn(code, charge.LastAdviceCode))
        {
            await transaction.Run(async c => Result.Success(await outcomes.DropWaiting(charge with { CampaignId = campaignId }, c)), ct);
            return;
        }

        var attemptNo = await settlements.TryStartAttempt(charge.PledgeId, charge.PledgePublicId, onSession: false, ct);
        if (attemptNo is not { } n)
        {
            return;
        }

        var outcome = await WithinTimeout(c => psp.Charge(
            new ChargeRequest(customer, charge.PaymentMethodRef, charge.AmountMinor, charge.Currency, charge.PledgePublicId, n),
            Charge.KeyFor(charge.PledgePublicId, n),
            c), ct);
        await Record(campaignId, charge, n, outcome, ct);
    }

    private async Task Resolve(Guid campaignId, ChargeWork charge, string customer, CancellationToken ct)
    {
        var n = charge.AttemptNo;
        var reference = new ChargeReference(charge.PspPaymentId, customer, charge.PledgePublicId, n);
        var outcome = await WithinTimeout(c => psp.LookUp(reference, c), ct);
        if (charge.OnSession)
        {
            // A payment the backer confirms waits for the backer, until the window ends: then it is
            // cancelled, and a backer who confirmed meanwhile is charged, never dropped.
            var windowOver = (charge.PaymentFailedAt ?? clock.GetUtcNow()) + dunning.Window <= clock.GetUtcNow();
            if (outcome is ChargeOutcome.Unknown { Reason: ChargeOutcome.WaitingForBacker } && windowOver)
            {
                outcome = await WithinTimeout(c => psp.Cancel(reference, c), ct);
            }
            else if (outcome is ChargeOutcome.NotFound)
            {
                outcome = new ChargeOutcome.Declined("authentication_required", null); // never created: the backer may confirm again
            }
        }
        else if (outcome is ChargeOutcome.NotFound)
        {
            // The first request never reached the PSP: send that same attempt again, with its own key.
            outcome = await WithinTimeout(c => psp.Charge(
                new ChargeRequest(customer, charge.PaymentMethodRef, charge.AmountMinor, charge.Currency, charge.PledgePublicId, n),
                Charge.KeyFor(charge.PledgePublicId, n),
                c), ct);
        }

        await Record(campaignId, charge, n, outcome, ct);
    }

    private async Task<ChargeOutcome> WithinTimeout(Func<CancellationToken, Task<ChargeOutcome>> call, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.PspCallTimeout);
        try
        {
            return await call(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ChargeOutcome.Unknown("the PSP did not answer in time"); // asked about again on the next pass
        }
    }

    private Task Record(Guid campaignId, ChargeWork charge, int n, ChargeOutcome outcome, CancellationToken cancellationToken) =>
        outcome is ChargeOutcome.Unknown
            ? Task.CompletedTask
            : transaction.Run(async ct => Result.Success(await outcomes.Record(charge with { CampaignId = campaignId }, n, outcome, ct)), cancellationToken);
}
