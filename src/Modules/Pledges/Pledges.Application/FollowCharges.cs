using BuildingBlocks.Application;

using Payments.Contracts;

using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// A pledge follows its charge: CHARGED on success. A decline inside the recovery window changes
/// nothing here; a dropped charge drops the pledge, writes its DROP entry and takes it out of the
/// snapshot, in this handler's one transaction (lesson s3-a04). The campaign stays funded.
/// </summary>
public sealed class FollowCharges(IPledges pledges, IPledgeLedger ledger, IFundingWindows windows)
    : IIntegrationEventHandler<PledgeChargeSucceeded>, IIntegrationEventHandler<PledgeChargeFailed>
{
    public Task Handle(PledgeChargeSucceeded succeeded, CancellationToken cancellationToken) =>
        pledges.TryRecordCharged(succeeded.PledgeId, cancellationToken);

    public async Task Handle(PledgeChargeFailed failed, CancellationToken cancellationToken)
    {
        if (!failed.Final || await pledges.TryDrop(failed.PledgeId, cancellationToken) is not { } dropped)
        {
            return;
        }

        await ledger.Append(LedgerEntry.Drop(dropped, $"charge-dropped:{dropped.PublicId}"), cancellationToken);
        await windows.TakeOutDropped(dropped.CampaignId, dropped.AmountMinor, cancellationToken);
    }
}
