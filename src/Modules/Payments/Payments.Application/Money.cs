using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Contracts;

using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;
using Payments.Domain;

namespace Payments.Application;

/// <summary>The money ledger: journals are added, never changed, and each one balances.</summary>
public interface IMoneyLedger
{
    /// <summary>
    /// Adds a journal and its lines, inside the caller's transaction. False: a journal of this kind
    /// from this source exists already, so a replay posts nothing. The database checks at commit
    /// that every journal sums to zero.
    /// </summary>
    Task<bool> Post(MoneyJournal journal, CancellationToken cancellationToken);

    /// <summary>The campaign's balance on each account it touched: debits positive, credits negative.</summary>
    Task<IReadOnlyDictionary<string, long>> Balances(Guid campaignId, CancellationToken cancellationToken);

    Task<bool> HasJournal(Guid campaignId, string kind, CancellationToken cancellationToken);
}

/// <summary>A settled campaign's way to its payout, one row per campaign, each move conditional.</summary>
public interface IPayouts
{
    /// <summary>Opens the campaign's payout as SETTLED, once.</summary>
    Task Open(Guid campaignId, CancellationToken cancellationToken);

    Task<Payout?> Find(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// The campaign's payout row, locked until the caller's transaction ends. The split and every
    /// refund take it first, so a refund is either wholly before the split or wholly after it.
    /// </summary>
    Task<Payout?> Lock(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Records what the split decided, once. False: it was recorded already.</summary>
    Task<bool> TrySetAmounts(Guid campaignId, long amountMinor, long reserveMinor, CancellationToken cancellationToken);

    /// <summary><paramref name="from"/> to PAID_OUT with the PSP's transfer. False: the payout had moved already.</summary>
    Task<bool> TryMarkPaidOut(Guid campaignId, PayoutStatus from, string? pspTransferId, CancellationToken cancellationToken);
}

/// <summary>Who is paid for a campaign: its creator, as Campaigns announced when it was published.</summary>
public interface ICampaignPayees
{
    Task Record(Guid campaignId, Guid creatorId, CancellationToken cancellationToken);

    Task<Guid?> Find(Guid campaignId, CancellationToken cancellationToken);
}

public sealed record RefundableCharge(Guid PledgeId, Guid CampaignId, long AmountMinor, string Currency, ChargeStatus Status, string? PspPaymentId, string? PspRefundId)
{
    public string PledgePublicId => PublicId.Format("plg", PledgeId);
}

/// <summary>A charge the PSP made that has no CHARGE journal: one charged before the money ledger existed.</summary>
public sealed record UnbookedCharge(Guid PledgeId, Guid CampaignId, long AmountMinor, string Currency, string PspPaymentId, string PspCustomerId, int AttemptNo)
{
    public string PledgePublicId => PublicId.Format("plg", PledgeId);
}

public interface IChargeJournals
{
    /// <summary>The campaign's charged (or since refunded) charges with no CHARGE journal.</summary>
    Task<IReadOnlyList<UnbookedCharge>> Unbooked(Guid campaignId, CancellationToken cancellationToken);
}

/// <summary>
/// Books the charges made before the money ledger existed. Their fee was never recorded, so the
/// PSP is asked for each payment (outside any transaction), and the journal is posted as the charge
/// would have posted it. Posting is idempotent, so running it twice changes nothing.
/// </summary>
public sealed class ChargeBookkeeping(
    IChargeJournals journals,
    IMoneyLedger ledger,
    IPaymentProvider psp,
    [FromKeyedServices(PaymentsModuleName.Value)] ITransactionScope transaction)
{
    /// <summary>True when every charge of the campaign has its journal.</summary>
    public async Task<Result<bool>> EnsureBooked(Guid campaignId, CancellationToken cancellationToken)
    {
        foreach (var charge in await journals.Unbooked(campaignId, cancellationToken))
        {
            ChargeOutcome outcome;
            try
            {
                outcome = await psp.LookUp(new ChargeReference(charge.PspPaymentId, charge.PspCustomerId, charge.PledgePublicId, charge.AttemptNo), cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Result.Failure<bool>(PaymentErrors.PspUnavailable);
            }

            if (outcome is not ChargeOutcome.Succeeded { FeeMinor: { } fee } made)
            {
                return Result.Failure<bool>(PaymentErrors.ChargeNotBooked);
            }

            await transaction.Run(async ct =>
            {
                await ledger.Post(MoneyJournal.ForCharge(charge.CampaignId, made.PspPaymentId, charge.AmountMinor, fee, charge.Currency), ct);
                return Result.Success(true);
            }, cancellationToken);
        }

        return Result.Success(true);
    }
}

public interface IChargeRefunds
{
    Task<RefundableCharge?> Find(Guid pledgeId, CancellationToken cancellationToken);

    /// <summary>CHARGED to REFUNDED, once.</summary>
    Task<bool> TryMarkRefunded(Guid pledgeId, string pspRefundId, CancellationToken cancellationToken);
}

/// <summary>Configuration section <c>Payments:Money</c>: the platform's fee and the reserve, in basis points.</summary>
public sealed class MoneyOptions
{
    public int PlatformFeeBasisPoints { get; set; } = 500;

    public int ReserveBasisPoints { get; set; } = 1_000;

    /// <summary>How often each task runs the money pass.</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Payments pays the campaign's creator; it learns who that is when the campaign is published.</summary>
public sealed class RecordPayee(ICampaignPayees payees) : IIntegrationEventHandler<CampaignPublished>
{
    public Task Handle(CampaignPublished published, CancellationToken cancellationToken) =>
        payees.Record(published.CampaignId, published.CreatorId, cancellationToken);
}

/// <summary>Pays a campaign's creator: the split, the reserve and the payout in the ledger first, then the PSP's transfer.</summary>
public sealed record PayOut(Guid CampaignId) : ICommand<PayoutDone>;

public sealed record PayoutDone(long AmountMinor, long ReserveMinor, string? PspTransferId);

/// <summary>
/// The ledger is written first, in one transaction: if the transfer then fails or its reply is
/// lost, the next call finds the amounts recorded and sends the same transfer again with the same
/// key, which the PSP answers with the transfer it made. A replay of a finished payout changes nothing.
/// </summary>
public sealed class PayOutHandler(
    IPayouts payouts,
    ICampaignPayees payees,
    IMoneyLedger ledger,
    IPaymentProvider psp,
    MoneyPolicy policy,
    ChargeBookkeeping bookkeeping,
    [FromKeyedServices(PaymentsModuleName.Value)] ITransactionScope transaction) : ICommandHandler<PayOut, PayoutDone>
{
    public async Task<Result<PayoutDone>> Handle(PayOut command, CancellationToken cancellationToken)
    {
        var id = command.CampaignId;
        var payout = await payouts.Find(id, cancellationToken);
        if (payout is null)
        {
            return Result.Failure<PayoutDone>(PaymentErrors.NotSettled);
        }

        if (payout.Status == PayoutStatus.PaidOut)
        {
            return Result.Success(new PayoutDone(payout.AmountMinor ?? 0, payout.ReserveMinor ?? 0, payout.PspTransferId));
        }

        if (payout.Status != PayoutRules.PayableFrom)
        {
            return Result.Failure<PayoutDone>(PaymentErrors.NotPayable);
        }

        var booked = await bookkeeping.EnsureBooked(id, cancellationToken);
        if (!booked.IsSuccess)
        {
            return Result.Failure<PayoutDone>(booked.Errors);
        }

        var split = await transaction.Run(ct => Split(payout, ct), cancellationToken);
        if (!split.IsSuccess)
        {
            return Result.Failure<PayoutDone>(split.Errors);
        }

        var (amount, reserve) = split.Value;
        string? transfer = null;
        if (amount > 0)
        {
            var creator = await payees.Find(id, cancellationToken)
                ?? throw new InvalidOperationException($"No creator is recorded for campaign {id}.");
            try
            {
                transfer = await psp.Transfer(creator, amount, payout.Currency, $"payout:{PublicId.Format("cmp", id)}", cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Result.Failure<PayoutDone>(PaymentErrors.PspUnavailable);
            }
        }

        await payouts.TryMarkPaidOut(id, PayoutRules.PayableFrom, transfer, cancellationToken);
        return Result.Success(new PayoutDone(amount, reserve, transfer));
    }

    private async Task<Result<(long Amount, long Reserve)>> Split(Payout payout, CancellationToken ct)
    {
        var id = payout.CampaignId;
        // Locked first: a refund waits for the split to commit, or the split waits for the refund.
        if (await payouts.Lock(id, ct) is { AmountMinor: { } recorded, ReserveMinor: { } held })
        {
            return Result.Success((recorded, held)); // posted by an earlier call; only the transfer is left
        }

        var balances = await ledger.Balances(id, ct);
        var clearing = -balances.GetValueOrDefault(Accounts.Clearing);
        var fees = balances.GetValueOrDefault(Accounts.ProcessingFees);
        long amount = 0, reserve = 0;
        if (clearing != 0)
        {
            var platformFee = policy.PlatformFee(clearing);
            var creatorShare = clearing - platformFee - fees;
            var split = MoneyJournal.ForSplit(id, payout.Currency, clearing, fees, platformFee);
            if (!split.IsSuccess)
            {
                return Result.Failure<(long, long)>(split.Errors);
            }

            await ledger.Post(split.Value, ct);
            reserve = Math.Max(0, policy.Reserve(creatorShare));
            if (reserve > 0)
            {
                await ledger.Post(MoneyJournal.ForReserve(id, payout.Currency, reserve).Value, ct);
            }

            amount = Math.Max(0, creatorShare - reserve);
            if (amount > 0)
            {
                await ledger.Post(MoneyJournal.ForPayout(id, payout.Currency, amount).Value, ct);
            }
        }

        if (!await payouts.TrySetAmounts(id, amount, reserve, ct)
            && await payouts.Find(id, ct) is { AmountMinor: { } first, ReserveMinor: { } firstHeld })
        {
            return Result.Success((first, firstHeld)); // another call recorded its amounts first: those stand
        }

        return Result.Success((amount, reserve));
    }
}

/// <summary>Gives a charged pledge back to its backer, and records it as a journal of its own.</summary>
public sealed record RefundPledge(Guid PledgeId) : ICommand<RefundDone>;

public sealed record RefundDone(string PspRefundId, long AmountMinor);

/// <summary>
/// The PSP is asked first, with a key per pledge, so a retry returns the same refund. Then one
/// transaction marks the charge REFUNDED and posts the refund: out of clearing before the split,
/// out of the reserve and then the creator's balance after it. Nothing already posted is changed.
/// </summary>
public sealed class RefundPledgeHandler(
    IChargeRefunds charges,
    IPayouts payouts,
    IMoneyLedger ledger,
    IPaymentProvider psp,
    [FromKeyedServices(PaymentsModuleName.Value)] ITransactionScope transaction) : ICommandHandler<RefundPledge, RefundDone>
{
    public async Task<Result<RefundDone>> Handle(RefundPledge command, CancellationToken cancellationToken)
    {
        var charge = await charges.Find(command.PledgeId, cancellationToken);
        if (charge is null)
        {
            return Result.Failure<RefundDone>(PaymentErrors.ChargeNotFound);
        }

        if (charge is { Status: ChargeStatus.Refunded, PspRefundId: { } done })
        {
            return Result.Success(new RefundDone(done, charge.AmountMinor));
        }

        if (charge.Status != ChargeStatus.Charged || charge.PspPaymentId is null)
        {
            return Result.Failure<RefundDone>(PaymentErrors.ChargeNotRefundable);
        }

        if (await payouts.Find(charge.CampaignId, cancellationToken) is null)
        {
            return Result.Failure<RefundDone>(PaymentErrors.NotSettled);
        }

        string refundId;
        try
        {
            refundId = await psp.Refund(charge.PspPaymentId, charge.AmountMinor, $"{charge.PledgePublicId}:refund", cancellationToken);
        }
        catch (HttpRequestException)
        {
            return Result.Failure<RefundDone>(PaymentErrors.PspUnavailable);
        }

        return await transaction.Run(async ct =>
        {
            // The same lock as the split: whether the refund comes before or after it is decided here.
            await payouts.Lock(charge.CampaignId, ct);
            if (await charges.TryMarkRefunded(charge.PledgeId, refundId, ct))
            {
                var afterSplit = await ledger.HasJournal(charge.CampaignId, JournalKinds.Split, ct);
                var reserveLeft = afterSplit ? -(await ledger.Balances(charge.CampaignId, ct)).GetValueOrDefault(Accounts.CreatorReserve) : 0;
                await ledger.Post(MoneyJournal.ForRefund(charge.CampaignId, refundId, charge.AmountMinor, charge.Currency, afterSplit, reserveLeft), ct);
            }

            return Result.Success(new RefundDone(refundId, charge.AmountMinor));
        }, cancellationToken);
    }
}

/// <summary>Where a campaign's money stands, account by account.</summary>
public sealed record GetCampaignMoney(Guid CampaignId) : IQuery<CampaignMoney>;

public sealed record CampaignMoney(string? Status, IReadOnlyDictionary<string, long> Balances);

/// <summary>
/// Loads from the writer: a creator who has just been paid out, or an operator checking a refund,
/// must see the balances as they are, not as a replica last saw them.
/// </summary>
[ReadsTheWriter]
public sealed class GetCampaignMoneyHandler(IPayouts payouts, IMoneyLedger ledger) : IQueryHandler<GetCampaignMoney, CampaignMoney>
{
    public async Task<Result<CampaignMoney>> Handle(GetCampaignMoney query, CancellationToken cancellationToken)
    {
        var payout = await payouts.Find(query.CampaignId, cancellationToken);
        return Result.Success(new CampaignMoney(payout?.Status.Name(), await ledger.Balances(query.CampaignId, cancellationToken)));
    }
}
