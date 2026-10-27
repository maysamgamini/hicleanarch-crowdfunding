using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;
using Pledges.Domain;

namespace Pledges.Application;

public sealed record ChangePledge(Guid PledgeId, Guid BackerId, long AmountMinor, Guid? TierId, string CauseId) : ICommand<PledgeView>;

public sealed record CancelPledge(Guid PledgeId, Guid BackerId, string CauseId) : ICommand<PledgeView>;

/// <summary>
/// A change is one transaction, like a pledge: the pledge row's lock, the fence and the snapshot on
/// the funding window (by the difference), the tier move, the pledge, a CHANGE entry and the event.
/// Locks are always taken in that order: pledge, window, tiers.
/// </summary>
public sealed class ChangePledgeHandler(
    IPledges pledges,
    IFundingWindows windows,
    ILastDayRule lastDay,
    ITierSlots tiers,
    IPledgeLedger ledger,
    TimeProvider clock,
    [FromKeyedServices(PledgesModuleName.Value)] IOutbox outbox) : ICommandHandler<ChangePledge, PledgeView>
{
    public async Task<Result<PledgeView>> Handle(ChangePledge command, CancellationToken cancellationToken)
    {
        var pledge = await pledges.FindForChange(command.PledgeId, cancellationToken);
        if (pledge is null || pledge.BackerId != command.BackerId)
        {
            return Result.Failure<PledgeView>(PledgeErrors.PledgeNotFound);
        }

        var (oldAmount, oldTier) = (pledge.AmountMinor, pledge.TierId);
        TierCounter? newTier = null;
        if (command.TierId is { } tierId)
        {
            newTier = await tiers.Find(tierId, cancellationToken);
            if (newTier is null || newTier.CampaignId != pledge.CampaignId)
            {
                return Result.Failure<PledgeView>(PledgeErrors.UnknownTier);
            }
        }

        var changed = pledge.Change(command.AmountMinor, newTier);
        if (!changed.IsSuccess)
        {
            return Result.Failure<PledgeView>(changed.Errors);
        }

        var delta = pledge.AmountMinor - oldAmount;
        if (!await windows.TryAccept(pledge.CampaignId, delta, backerDelta: 0, cancellationToken))
        {
            return Result.Failure<PledgeView>(PledgeErrors.CampaignClosed);
        }

        if (delta < 0 && !await lastDay.Allows(pledge.CampaignId, -delta, cancellationToken))
        {
            return Result.Failure<PledgeView>(PledgeErrors.BelowGoalOnLastDay);
        }

        if (!await tiers.TryMove(oldTier, pledge.TierId, cancellationToken))
        {
            return Result.Failure<PledgeView>(PledgeErrors.TierSoldOut);
        }

        if (!await pledges.TrySaveFromActive(pledge, cancellationToken))
        {
            return Result.Failure<PledgeView>(PledgeErrors.NotActive);
        }

        if (delta != 0)
        {
            await ledger.Append(LedgerEntry.Change(pledge, delta, command.CauseId), cancellationToken);
        }

        await outbox.Add(new PledgeChanged(Guid.CreateVersion7(), clock.GetUtcNow(), pledge.Id, pledge.CampaignId, pledge.BackerId,
            oldAmount, pledge.AmountMinor, oldTier, pledge.TierId), cancellationToken);
        return Result.Success(PledgeViews.Of(pledge));
    }
}

/// <summary>A cancel takes the pledge's whole amount and its backer out of the total, and gives its tier place back.</summary>
public sealed class CancelPledgeHandler(
    IPledges pledges,
    IFundingWindows windows,
    ILastDayRule lastDay,
    ITierSlots tiers,
    IPledgeLedger ledger,
    TimeProvider clock,
    [FromKeyedServices(PledgesModuleName.Value)] IOutbox outbox) : ICommandHandler<CancelPledge, PledgeView>
{
    public async Task<Result<PledgeView>> Handle(CancelPledge command, CancellationToken cancellationToken)
    {
        var pledge = await pledges.FindForChange(command.PledgeId, cancellationToken);
        if (pledge is null || pledge.BackerId != command.BackerId)
        {
            return Result.Failure<PledgeView>(PledgeErrors.PledgeNotFound);
        }

        var cancelled = pledge.Cancel();
        if (!cancelled.IsSuccess)
        {
            return Result.Failure<PledgeView>(cancelled.Errors);
        }

        if (!await windows.TryAccept(pledge.CampaignId, -pledge.AmountMinor, backerDelta: -1, cancellationToken))
        {
            return Result.Failure<PledgeView>(PledgeErrors.CampaignClosed);
        }

        if (!await lastDay.Allows(pledge.CampaignId, pledge.AmountMinor, cancellationToken))
        {
            return Result.Failure<PledgeView>(PledgeErrors.BelowGoalOnLastDay);
        }

        await tiers.TryMove(pledge.TierId, null, cancellationToken);
        if (!await pledges.TrySaveFromActive(pledge, cancellationToken))
        {
            return Result.Failure<PledgeView>(PledgeErrors.NotActive);
        }

        await ledger.Append(LedgerEntry.Cancel(pledge, command.CauseId), cancellationToken);
        await outbox.Add(new PledgeCancelled(Guid.CreateVersion7(), clock.GetUtcNow(), pledge.Id, pledge.CampaignId, pledge.BackerId,
            pledge.AmountMinor), cancellationToken);
        return Result.Success(PledgeViews.Of(pledge));
    }
}
