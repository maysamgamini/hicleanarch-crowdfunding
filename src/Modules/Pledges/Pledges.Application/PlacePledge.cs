using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;
using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// A pledge with a saved card. <see cref="CauseId"/> names the request, so its ledger entry can be
/// traced back to it.
/// </summary>
public sealed record PlacePledge(
    Guid CampaignId,
    Guid BackerId,
    Guid? TierId,
    long AmountMinor,
    string? PaymentMethodRef,
    string CauseId) : ICommand<PledgePlaced>;

public sealed record PledgePlaced(string PledgeId, string Status);

/// <summary>
/// One transaction, opened by the request's idempotency key: the fence and the snapshot on the
/// funding window, the pledge, its ledger entry and its event. Each step is a rule's port, checked
/// in the order the API promises (lesson s1-a03: closed, amount, already backing, sold out); any
/// refusal rolls all of it back.
/// </summary>
public sealed class PlacePledgeHandler(
    IFundingWindows windows,
    ITierSlots tiers,
    IPledges pledges,
    IPledgeLedger ledger,
    TimeProvider clock,
    [FromKeyedServices(PledgesModuleName.Value)] IOutbox outbox) : ICommandHandler<PlacePledge, PledgePlaced>
{
    public async Task<Result<PledgePlaced>> Handle(PlacePledge command, CancellationToken cancellationToken)
    {
        var window = await windows.Find(command.CampaignId, cancellationToken);
        if (window is null)
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.CampaignNotFound);
        }

        // Lesson 7's fence, pledge side: closed by status or by the database's clock.
        if (!await windows.TryAccept(command.CampaignId, command.AmountMinor, backerDelta: 1, cancellationToken))
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.CampaignClosed);
        }

        TierCounter? tier = null;
        if (command.TierId is { } tierId)
        {
            tier = await tiers.Find(tierId, cancellationToken);
            if (tier is null || tier.CampaignId != command.CampaignId)
            {
                return Result.Failure<PledgePlaced>(PledgeErrors.UnknownTier);
            }
        }

        var pledge = Pledge.WithSavedCard(command.CampaignId, command.BackerId, tier, command.AmountMinor, command.PaymentMethodRef);
        if (!pledge.IsSuccess)
        {
            return Result.Failure<PledgePlaced>(pledge.Errors);
        }

        // Lesson 4's second layer: one live pledge per backer per campaign.
        if (!await pledges.TryAdd(pledge.Value, cancellationToken))
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.AlreadyBacking);
        }

        // Lesson 6: the conditional update is what keeps the limit, whatever the domain read before.
        if (tier is { IsLimited: true } && !await tiers.TryClaim(tier.TierId, cancellationToken))
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.TierSoldOut);
        }

        await ledger.Append(LedgerEntry.Activate(pledge.Value, command.CauseId), cancellationToken);
        await outbox.Add(new PledgeActivated(
            Guid.CreateVersion7(),
            clock.GetUtcNow(),
            pledge.Value.Id,
            pledge.Value.CampaignId,
            pledge.Value.BackerId,
            pledge.Value.TierId,
            pledge.Value.AmountMinor,
            window.Currency), cancellationToken);
        return Result.Success(new PledgePlaced(pledge.Value.PublicId, "ACTIVE"));
    }
}
