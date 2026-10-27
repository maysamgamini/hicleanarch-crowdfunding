using Campaigns.Domain;

namespace Campaigns.UnitTests;

public class PublishRulesTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static Campaign Draft(DateTimeOffset deadline, params TierDraft[] tiers) =>
        Campaign.CreateDraft(Guid.NewGuid(), "Game", Money.Create(5_000_000, "USD").Value, deadline, tiers).Value;

    private static readonly TierDraft EarlyBird = new("Early Bird", 4_900, 500);

    [Fact]
    public void A_complete_draft_becomes_live()
    {
        var campaign = Draft(Now.AddDays(30), EarlyBird);

        var result = campaign.Publish(Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(CampaignStatus.Live, campaign.Status);
        Assert.Equal(Now, campaign.PublishedAt);
    }

    [Fact]
    public void A_live_campaign_is_not_published_again()
    {
        var campaign = Draft(Now.AddDays(30), EarlyBird);
        campaign.Publish(Now);

        var again = campaign.Publish(Now);

        Assert.Equal("not_a_draft", Assert.Single(again.Errors).Code);
    }

    [Fact]
    public void A_deadline_that_has_passed_is_refused()
    {
        var campaign = Draft(Now.AddMinutes(-1), EarlyBird);

        Assert.Contains(campaign.Publish(Now).Errors, e => e.Code == "deadline_not_in_future");
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
    }

    [Fact]
    public void A_deadline_exactly_now_is_refused_because_it_is_half_open() =>
        Assert.Contains(Draft(Now, EarlyBird).Publish(Now).Errors, e => e.Code == "deadline_not_in_future");

    [Fact]
    public void A_campaign_without_tiers_is_refused() =>
        Assert.Contains(Draft(Now.AddDays(30)).Publish(Now).Errors, e => e.Code == "no_tiers");

    [Fact]
    public void Every_broken_publish_rule_is_reported_at_once() =>
        Assert.Equal(["deadline_not_in_future", "no_tiers"], Draft(Now.AddDays(-1)).Publish(Now).Errors.Select(e => e.Code));
}
