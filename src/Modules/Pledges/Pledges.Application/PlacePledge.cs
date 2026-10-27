using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;
using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// A pledge, with a saved card or with a new one. <see cref="CauseId"/> names the request, so its
/// ledger entry can be traced back to it. A new card's setup was started at the PSP before this
/// command's transaction opened; <see cref="NewCard"/> carries the pledge id it was started for and
/// the secret the PSP's card form needs.
/// </summary>
public sealed record PlacePledge(
    Guid CampaignId,
    Guid BackerId,
    Guid? TierId,
    long AmountMinor,
    string? PaymentMethodRef,
    string CauseId,
    NewCard? NewCard = null) : ICommand<PledgePlaced>;

/// <summary>A card setup already started at the PSP for the pledge that will have <see cref="PledgeId"/>.</summary>
public sealed record NewCard(Guid PledgeId, string ClientSecret);

/// <summary>A new-card pledge also gets when its hold ends and the secret the PSP's card form needs.</summary>
public sealed record PledgePlaced(string PledgeId, string Status, DateTimeOffset? HoldExpiresAt = null, string? ClientSecret = null);

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
    /// <summary>How long a place is kept while a backer types a card (lesson 6).</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(15);

    public async Task<Result<PledgePlaced>> Handle(PlacePledge command, CancellationToken cancellationToken)
    {
        var window = await windows.Find(command.CampaignId, cancellationToken);
        if (window is null)
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.CampaignNotFound);
        }

        if (command.PaymentMethodRef is null)
        {
            return command.NewCard is { } newCard
                ? await WithNewCard(command, newCard, cancellationToken)
                : Result.Failure<PledgePlaced>(PledgeErrors.CardSetupFailed);
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

    /// <summary>
    /// Lesson 1's second step: the pledge waits for its card and does not count yet, but it keeps a
    /// tier place for 15 minutes. The card setup was started before this transaction; nothing here
    /// calls the PSP.
    /// </summary>
    private async Task<Result<PledgePlaced>> WithNewCard(PlacePledge command, NewCard newCard, CancellationToken cancellationToken)
    {
        if (!await windows.IsOpen(command.CampaignId, cancellationToken))
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

        var pledge = Pledge.WaitingForCard(newCard.PledgeId, command.CampaignId, command.BackerId, tier, command.AmountMinor, clock.GetUtcNow() + HoldFor);
        if (!pledge.IsSuccess)
        {
            return Result.Failure<PledgePlaced>(pledge.Errors);
        }

        if (!await pledges.TryAdd(pledge.Value, cancellationToken))
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.AlreadyBacking);
        }

        if (tier is { IsLimited: true } && !await tiers.TryHold(tier.TierId, cancellationToken))
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.TierSoldOut);
        }

        return Result.Success(new PledgePlaced(pledge.Value.PublicId, "PENDING_SETUP", pledge.Value.HoldExpiresAt, newCard.ClientSecret));
    }
}
