using Campaigns.Domain;

namespace Campaigns.UnitTests;

public class DraftRulesTests
{
    private static readonly Guid Creator = Guid.NewGuid();

    private static readonly DateTimeOffset Deadline = new(2027, 3, 31, 23, 59, 0, TimeSpan.FromHours(-4));

    private static Money Usd(long minor) => Money.Create(minor, "USD").Value;

    [Fact]
    public void A_valid_draft_is_a_draft_with_its_deadline_in_utc()
    {
        var result = Campaign.CreateDraft(Creator, " Harbor Lights ", Usd(5_000_000), Deadline,
            [new TierDraft("Early Bird", 4_900, 500), new TierDraft("Standard copy", 5_900, null)]);

        Assert.True(result.IsSuccess);
        var campaign = result.Value;
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Equal("Harbor Lights", campaign.Title);
        Assert.Equal(new DateTimeOffset(2027, 4, 1, 3, 59, 0, TimeSpan.Zero), campaign.Deadline);
        Assert.Equal(TimeSpan.Zero, campaign.Deadline.Offset);
        Assert.Equal(2, campaign.Tiers.Count);
    }

    [Fact]
    public void A_goal_of_zero_is_refused() =>
        AssertRefused(Campaign.CreateDraft(Creator, "Game", Usd(0), Deadline, []), "goal_not_positive");

    [Fact]
    public void A_tier_with_no_minimum_is_refused() =>
        AssertRefused(Campaign.CreateDraft(Creator, "Game", Usd(100), Deadline, [new TierDraft("Free", 0, null)]), "tier_minimum_not_positive");

    [Fact]
    public void A_tier_limit_of_zero_is_refused() =>
        AssertRefused(Campaign.CreateDraft(Creator, "Game", Usd(100), Deadline, [new TierDraft("None", 100, 0)]), "tier_limit_not_positive");

    [Fact]
    public void Two_tiers_with_the_same_title_are_refused() =>
        AssertRefused(Campaign.CreateDraft(Creator, "Game", Usd(100), Deadline,
            [new TierDraft("Early Bird", 100, 5), new TierDraft("early bird ", 200, null)]), "duplicate_tier_title");

    [Fact]
    public void Every_broken_rule_is_reported_at_once()
    {
        var result = Campaign.CreateDraft(Creator, "Game", Usd(0), Deadline, [new TierDraft("A", 0, 0)]);

        Assert.Equal(["goal_not_positive", "tier_minimum_not_positive", "tier_limit_not_positive"],
            result.Errors.Select(e => e.Code));
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("US1")]
    public void A_currency_that_is_not_three_upper_case_letters_is_refused(string currency) =>
        AssertRefused(Money.Create(100, currency), "bad_currency");

    [Fact]
    public void Amounts_are_whole_minor_units_and_never_negative() =>
        AssertRefused(Money.Create(-1, "USD"), "negative_amount");

    private static void AssertRefused<T>(BuildingBlocks.Domain.Result<T> result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Code == code);
    }
}
