using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Payments.Contracts;

using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// The card form for a pledge that is still waiting for its card: asked for again when the
/// card-setup lane was full at pledge time. The lane's 429 reaches the backer with its wait.
/// </summary>
public sealed record ResumeCardSetup(Guid PledgeId, Guid BackerId) : ICommand<PledgePlaced>;

public sealed class ResumeCardSetupHandler(IPledges pledges, IDispatcher dispatcher, TimeProvider clock) : ICommandHandler<ResumeCardSetup, PledgePlaced>
{
    public async Task<Result<PledgePlaced>> Handle(ResumeCardSetup command, CancellationToken cancellationToken)
    {
        var pledge = await pledges.Find(command.PledgeId, cancellationToken);
        if (pledge is null || pledge.BackerId != command.BackerId)
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.PledgeNotFound);
        }

        if (pledge.Status != PledgeStatus.PendingSetup || pledge.HoldExpiresAt <= clock.GetUtcNow())
        {
            return Result.Failure<PledgePlaced>(PledgeErrors.NotWaitingForCard);
        }

        var setup = await dispatcher.Send<StartCardSetup, CardSetupStarted>(new StartCardSetup(pledge.Id, pledge.BackerId), cancellationToken);
        if (!setup.IsSuccess)
        {
            return setup.Errors[0].RetryAfter is not null
                ? Result.Failure<PledgePlaced>(setup.Errors)
                : Result.Failure<PledgePlaced>(PledgeErrors.CardSetupFailed);
        }

        return Result.Success(new PledgePlaced(pledge.PublicId, "PENDING_SETUP", pledge.HoldExpiresAt, setup.Value.ClientSecret));
    }
}
