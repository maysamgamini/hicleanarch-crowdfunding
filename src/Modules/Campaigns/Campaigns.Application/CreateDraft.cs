using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Domain;

namespace Campaigns.Application;

public sealed record CreateDraft(
    string Title,
    long GoalMinor,
    string Currency,
    DateTimeOffset Deadline,
    IReadOnlyList<TierDraft> Tiers) : ICommand<CampaignId>;

/// <summary>Is the request well formed? Lengths and required fields; the rules are the domain's.</summary>
public sealed class CreateDraftValidator : IValidator<CreateDraft>
{
    public const int MaxTitleLength = 120;

    public IEnumerable<Error> Validate(CreateDraft request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > MaxTitleLength)
        {
            yield return Error.Validation("bad_title", $"The title is required, at most {MaxTitleLength} characters.");
        }

        if (request.Tiers.Any(t => string.IsNullOrWhiteSpace(t.Title) || t.Title.Trim().Length > MaxTitleLength))
        {
            yield return Error.Validation("bad_tier_title", $"Every tier needs a title, at most {MaxTitleLength} characters.");
        }
    }
}

public sealed class CreateDraftHandler(ICampaignRepository campaigns) : ICommandHandler<CreateDraft, CampaignId>
{
    public async Task<Result<CampaignId>> Handle(CreateDraft command, CancellationToken cancellationToken)
    {
        var goal = Money.Create(command.GoalMinor, command.Currency);
        if (!goal.IsSuccess)
        {
            return Result.Failure<CampaignId>(goal.Errors);
        }

        var draft = Campaign.CreateDraft(command.Title, goal.Value, command.Deadline, command.Tiers);
        if (!draft.IsSuccess)
        {
            return Result.Failure<CampaignId>(draft.Errors);
        }

        await campaigns.Add(draft.Value, cancellationToken);
        return Result.Success(draft.Value.Id);
    }
}
