using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;

using Pledges.Contracts;
using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// The PSP saved the card: the waiting pledge becomes active and counts, in one transaction with the
/// fence, the held place becoming claimed, a ledger entry caused by the PSP's message, and the
/// event. A card saved after the hold ran out activates nothing: EXPIRED is final, and the backer
/// pledges again with the saved card.
/// </summary>
public sealed class ActivatePledge(
    IPledges pledges,
    IFundingWindows windows,
    ITierSlots tiers,
    IPledgeLedger ledger,
    TimeProvider clock,
    [FromKeyedServices(PledgesModuleName.Value)] IOutbox outbox) : IIntegrationEventHandler<PaymentMethodSaved>
{
    public async Task Handle(PaymentMethodSaved saved, CancellationToken cancellationToken)
    {
        var pledge = await pledges.FindForChange(saved.PledgeId, cancellationToken);
        if (pledge is null || !await pledges.TryActivate(pledge.Id, saved.PaymentMethodRef, cancellationToken))
        {
            return;
        }

        var limited = pledge.TierId is { } tierId && (await tiers.Find(tierId, cancellationToken))?.IsLimited == true;

        // Lesson 7's fence: the pledge counts only while pledging is open.
        if (!await windows.TryAccept(pledge.CampaignId, pledge.AmountMinor, backerDelta: 1, cancellationToken))
        {
            await pledges.TryExpireActivated(pledge.Id, cancellationToken);
            if (limited)
            {
                await tiers.ReleaseHold(pledge.TierId!.Value, cancellationToken);
            }

            return;
        }

        if (limited)
        {
            await tiers.HoldToClaim(pledge.TierId!.Value, cancellationToken);
        }

        var window = await windows.Find(pledge.CampaignId, cancellationToken);
        await ledger.Append(LedgerEntry.Activate(pledge, $"psp:{saved.PspMessageId}"), cancellationToken);
        await outbox.Add(new PledgeActivated(
            Guid.CreateVersion7(), clock.GetUtcNow(), pledge.Id, pledge.CampaignId, pledge.BackerId, pledge.TierId, pledge.AmountMinor, window!.Currency),
            cancellationToken);
    }
}

public sealed record ExpireHolds : ICommand<int>;

/// <summary>The sweeper's pass: waiting pledges whose 15 minutes are up expire and give their places back.</summary>
public sealed class ExpireHoldsHandler(IHolds holds) : ICommandHandler<ExpireHolds, int>
{
    public async Task<BuildingBlocks.Domain.Result<int>> Handle(ExpireHolds command, CancellationToken cancellationToken) =>
        BuildingBlocks.Domain.Result.Success(await holds.ExpireDue(100, cancellationToken));
}
