using BuildingBlocks.Application;

using Payments.Contracts;

namespace Pledges.Application;

/// <summary>A pledge follows its charge: CHARGED on success; FAILED only when no attempt is left.</summary>
public sealed class FollowCharges(IPledges pledges) : IIntegrationEventHandler<PledgeChargeSucceeded>, IIntegrationEventHandler<PledgeChargeFailed>
{
    public Task Handle(PledgeChargeSucceeded succeeded, CancellationToken cancellationToken) =>
        pledges.TryRecordCharge(succeeded.PledgeId, charged: true, cancellationToken);

    public Task Handle(PledgeChargeFailed failed, CancellationToken cancellationToken) =>
        failed.Final ? pledges.TryRecordCharge(failed.PledgeId, charged: false, cancellationToken) : Task.CompletedTask;
}
