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
    /// Takes a turn at one job that is not done: <c>FOR UPDATE SKIP LOCKED</c>, skipping jobs whose
    /// <c>not_before</c> or pause is still ahead, the job whose last turn was longest ago first, so
    /// campaigns take turns. A turn is not exclusive: several workers can be at one campaign at once,
    /// each with charges of its own. With <paramref name="campaignId"/>, only that campaign's job,
    /// whatever its <c>not_before</c>. Null: nothing to do.
    /// </summary>
    Task<Guid?> TakeTurn(Guid? campaignId, IReadOnlyCollection<Guid>? among, CancellationToken cancellationToken);

    /// <summary>
    /// Claims for this worker the next batch of the campaign's charges that need work now, at most
    /// <paramref name="batchSize"/>, oldest first: TO_CHARGE and PAYMENT_FAILED ones whose attempt is
    /// due, and CHARGING ones whose look-up is due. <c>FOR UPDATE SKIP LOCKED</c> and a lease on each
    /// charge, so workers at the same campaign never get the same charge.
    /// </summary>
    Task<IReadOnlyList<ChargeWork>> ClaimCharges(Guid campaignId, string worker, TimeSpan lease, int batchSize, CancellationToken cancellationToken);

    /// <summary>Extends this worker's lease on one charge. False: the charge was taken over by another worker.</summary>
    Task<bool> Renew(Guid pledgeId, string worker, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Extends this worker's lease on every charge of the campaign it still holds, while a long batch waits for its turn at the budget.</summary>
    Task RenewAll(Guid campaignId, string worker, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Gives back the charges this worker holds for the campaign, and sets when the job is next worth
    /// a turn: now if a charge is due, else the earliest time one is (its next attempt, its next
    /// look-up, or the end of another worker's lease), or <paramref name="unknownBackoff"/> while charges
    /// are still to be created. With <paramref name="pause"/>, no turn before it has passed.
    /// </summary>
    Task Release(Guid campaignId, string worker, TimeSpan unknownBackoff, TimeSpan pause, CancellationToken cancellationToken);

    /// <summary>Gives back every charge this worker holds, at once: the worker is stopping.</summary>
    Task ReleaseAll(string worker, CancellationToken cancellationToken);

    /// <summary>To DONE, once. False: another worker finished it first.</summary>
    Task<bool> TryFinish(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// When a CHARGING charge whose outcome is not known yet is next looked up, if attempt
    /// <paramref name="attemptNo"/> is still its latest: soon after a timeout, at the window's end
    /// for a payment that waits for the backer.
    /// </summary>
    Task LookUpAt(Guid pledgeId, int attemptNo, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// The job's next turn is due now: an outcome arrived from outside a pass, by webhook, and may
    /// have ended its last open charge while the job waited for that charge's look-up days ahead.
    /// </summary>
    Task WakeUp(Guid campaignId, CancellationToken cancellationToken);

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
    /// IN_FLIGHT with its key, committed in a transaction of its own before the PSP is called: never
    /// inside a caller's. Null: another worker, or the backer, has it. <paramref name="lookUpAt"/>:
    /// when the attempt is first looked up if nothing has settled it; null, as soon as a worker's lease ends.
    /// </summary>
    Task<int?> TryStartAttempt(Guid pledgeId, string pledgePublicId, bool onSession, DateTimeOffset? lookUpAt, CancellationToken cancellationToken);

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

    /// <summary>Charges one turn claims and works through before the worker takes its next turn.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// The most PSP calls one worker has in flight while it works its batch. The shared budget, not
    /// this, sets the pace: it only has to be enough for the worker's share of the rate at the PSP's
    /// latency (lesson s3-a04: 250 a second at 0.5 s is 125 in flight across every task).
    /// </summary>
    public int CallsInFlight { get; set; } = 25;

    /// <summary>How long a charge whose outcome is unknown waits before the PSP is asked about it again, and a job whose charges are still being created.</summary>
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

/// <summary>How far a campaign's settlement has got, asked by <see cref="CallerId"/>.</summary>
public sealed record GetSettlementProgress(Guid CampaignId, Guid CallerId) : IQuery<SettlementProgress>;

/// <summary>
/// Progress, and an ETA from the charges' share of the budget: the rate is the PSP's, not ours. Only
/// the campaign's creator may ask, as Payments' own payee record names them, so the answer never
/// depends on asking Campaigns; anyone else is told there is no settlement, which says nothing about
/// whether the campaign exists. Read from the writer: Payments keeps no read side.
/// </summary>
[ReadsTheWriter]
public sealed class GetSettlementProgressHandler(ISettlements settlements, ICampaignPayees payees, PspBudget budget) : IQueryHandler<GetSettlementProgress, SettlementProgress>
{
    public async Task<Result<SettlementProgress>> Handle(GetSettlementProgress query, CancellationToken cancellationToken)
    {
        if (await payees.Find(query.CampaignId, cancellationToken) != query.CallerId)
        {
            return Result.Failure<SettlementProgress>(PaymentErrors.NoSettlement);
        }

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
/// One pass of a settlement worker: take a turn at a job, claim a batch of its charges, work them
/// with calls in flight, finish the job or give the charges back. With <see cref="CampaignId"/>, only
/// that campaign's job, whenever it is due (an operator's or a test's pass). With <see cref="Among"/>,
/// the next due job of those campaigns, in turn. True if the pass did something: worked a charge or
/// finished the job.
/// </summary>
public sealed record RunSettlement(string Worker, Guid? CampaignId = null, IReadOnlyCollection<Guid>? Among = null) : ICommand<bool>;

/// <summary>
/// The orchestrator. Workers share a campaign by claiming its charges, so its pace is the shared
/// budget's, not one worker's latency: a pass keeps up to <see cref="SettlementOptions.CallsInFlight"/>
/// calls waiting for their tokens. The leases only decide who works; what is true is decided by the
/// conditional moves and the PSP's idempotency keys.
/// </summary>
public sealed class RunSettlementHandler(
    ISettlements settlements,
    IPayouts payouts,
    SettlementOptions options,
    TimeProvider clock,
    IServiceScopeFactory scopes,
    [FromKeyedServices(PaymentsModuleName.Value)] IOutbox outbox,
    [FromKeyedServices(PaymentsModuleName.Value)] ITransactionScope transaction) : ICommandHandler<RunSettlement, bool>
{
    public async Task<Result<bool>> Handle(RunSettlement command, CancellationToken cancellationToken)
    {
        var settings = options;
        var campaignId = await settlements.TakeTurn(command.CampaignId, command.Among, cancellationToken);
        if (campaignId is not { } id)
        {
            return Result.Success(false);
        }

        var batch = await settlements.ClaimCharges(id, command.Worker, settings.Lease, settings.BatchSize, cancellationToken);
        var worked = 0;
        var pspFailed = 0;
        using (var keeping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var renewals = KeepLeases(id, command.Worker, keeping.Token);
            try
            {
                // A DbContext serves one operation at a time, so each charge in flight works in a scope of its own.
                await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = settings.CallsInFlight, CancellationToken = cancellationToken }, async (charge, ct) =>
                {
                    if (Volatile.Read(ref pspFailed) == 1)
                    {
                        return; // the PSP is failing: the rest of the batch waits for the pause
                    }

                    await using var scope = scopes.CreateAsyncScope();
                    try
                    {
                        if (await scope.ServiceProvider.GetRequiredService<SettlementStep>().Work(id, command.Worker, charge, ct))
                        {
                            Interlocked.Increment(ref worked);
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // The PSP cannot be reached, or the circuit is open. A charge left CHARGING is
                        // resolved with its own key on a later turn.
                        Volatile.Write(ref pspFailed, 1);
                    }
                });
            }
            finally
            {
                await keeping.CancelAsync();
                await renewals;
            }
        }

        if (pspFailed == 1)
        {
            // Nothing got through: the job pauses. If some calls did, as a half-open circuit's one
            // trial call does, the charges it refused wait only for the next turn.
            await settlements.Release(id, command.Worker, settings.UnknownBackoff, worked == 0 ? settings.PauseWhenPspFails : TimeSpan.Zero, cancellationToken);
            return Result.Success(worked > 0);
        }

        var counts = await settlements.Counts(id, cancellationToken);
        if (counts.Open == 0 && counts.Charged + counts.Failed >= counts.Expected)
        {
            var finished = await transaction.Run(async ct =>
            {
                if (!await settlements.TryFinish(id, ct))
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

    /// <summary>
    /// A batch can wait longer than a lease for its tokens when many workers share the budget, so the
    /// charges still waiting are renewed every third of a lease. Each charge is renewed again on its own
    /// just before its call, which is what keeps a worker from calling for a charge it no longer holds.
    /// </summary>
    private async Task KeepLeases(Guid campaignId, string worker, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.Lease / 3, clock, ct);
                await settlements.RenewAll(campaignId, worker, options.Lease, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the batch is done
        }
    }
}

/// <summary>
/// One charge of a worker's batch. Every attempt is committed before the PSP is called. A timeout
/// leaves it IN_FLIGHT, and a later turn asks the PSP about that same attempt before anything else:
/// a new attempt, with a new key, is made only after the last one is known to have failed.
/// </summary>
public sealed class SettlementStep(
    ISettlements settlements,
    IPaymentProvider psp,
    SettlementOptions options,
    ChargeOutcomes outcomes,
    DunningPolicy dunning,
    TimeProvider clock,
    [FromKeyedServices(PaymentsModuleName.Value)] ITransactionScope transaction)
{
    /// <summary>False: the charge was taken over by another worker, and nothing was sent for it.</summary>
    public async Task<bool> Work(Guid campaignId, string worker, ChargeWork charge, CancellationToken ct)
    {
        if (!await settlements.Renew(charge.PledgeId, worker, options.Lease, ct))
        {
            return false; // the other worker carries on from the charge's status
        }

        var customer = await settlements.CustomerOf(charge.BackerId, ct);
        if (charge.Status == ChargeStatus.Charging)
        {
            await Resolve(campaignId, charge, customer, ct);
        }
        else
        {
            await ChargeOnce(campaignId, charge, customer, ct);
        }

        return true;
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

        var attemptNo = await settlements.TryStartAttempt(charge.PledgeId, charge.PledgePublicId, onSession: false, lookUpAt: null, ct);
        if (attemptNo is not { } n)
        {
            return;
        }

        var outcome = await WithinTimeout(c => psp.Charge(
            new ChargeRequest(customer, charge.PaymentMethodRef, charge.AmountMinor, charge.Currency, charge.PledgePublicId, n),
            Charge.KeyFor(charge.PledgePublicId, n),
            c), ct);
        await Record(campaignId, charge, n, outcome, clock.GetUtcNow() + options.UnknownBackoff, ct);
    }

    private async Task Resolve(Guid campaignId, ChargeWork charge, string customer, CancellationToken ct)
    {
        var n = charge.AttemptNo;
        var reference = new ChargeReference(charge.PspPaymentId, customer, charge.PledgePublicId, n);
        var outcome = await WithinTimeout(c => psp.LookUp(reference, c), ct);
        var lookAgain = clock.GetUtcNow() + options.UnknownBackoff;
        if (charge.OnSession)
        {
            // A payment the backer confirms waits for the backer, until the window ends: then it is
            // cancelled, and a backer who confirmed meanwhile is charged, never dropped. Until then
            // its webhook settles it, and it is not looked up again before the window's end.
            var windowEnds = (charge.PaymentFailedAt ?? clock.GetUtcNow()) + dunning.Window;
            if (outcome is ChargeOutcome.Unknown { Reason: ChargeOutcome.WaitingForBacker })
            {
                if (windowEnds <= clock.GetUtcNow())
                {
                    outcome = await WithinTimeout(c => psp.Cancel(reference, c), ct);
                }
                else
                {
                    lookAgain = windowEnds;
                }
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

        await Record(campaignId, charge, n, outcome, lookAgain, ct);
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
            return new ChargeOutcome.Unknown("the PSP did not answer in time"); // asked about again on a later turn
        }
    }

    private Task Record(Guid campaignId, ChargeWork charge, int n, ChargeOutcome outcome, DateTimeOffset lookAgain, CancellationToken cancellationToken) =>
        outcome is ChargeOutcome.Unknown
            ? settlements.LookUpAt(charge.PledgeId, n, lookAgain, cancellationToken)
            : transaction.Run(async ct => Result.Success(await outcomes.Record(charge with { CampaignId = campaignId }, n, outcome, ct)), cancellationToken);
}
