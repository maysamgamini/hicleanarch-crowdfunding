using Payments.Domain;

namespace Payments.UnitTests;

/// <summary>Lesson s3-a04's recovery window as a rule, with no database: when to try again, and when to drop.</summary>
public class DunningPolicyTests
{
    private static readonly DateTimeOffset FailedAt = new(2027, 4, 1, 4, 0, 0, TimeSpan.Zero);

    private static readonly DunningPolicy Policy = DunningPolicy.Default;

    [Fact]
    public void A_soft_decline_is_retried_on_days_one_three_and_five_then_once_more_at_the_end()
    {
        Assert.Equal((false, FailedAt.AddDays(1)), Policy.AfterDecline(1, "insufficient_funds", FailedAt, FailedAt));
        Assert.Equal((false, FailedAt.AddDays(3)), Policy.AfterDecline(2, "insufficient_funds", FailedAt, FailedAt.AddDays(1)));
        Assert.Equal((false, FailedAt.AddDays(5)), Policy.AfterDecline(3, "insufficient_funds", FailedAt, FailedAt.AddDays(3)));
        Assert.Equal((false, FailedAt.AddDays(7)), Policy.AfterDecline(4, "insufficient_funds", FailedAt, FailedAt.AddDays(5)));
        Assert.Equal((true, (DateTimeOffset?)null), Policy.AfterDecline(5, "insufficient_funds", FailedAt, FailedAt.AddDays(7)));
    }

    [Theory]
    [InlineData("expired_card")]
    [InlineData("stolen_card")]
    [InlineData("authentication_required")]
    public void A_decline_only_the_backer_can_fix_waits_for_the_final_attempt_at_the_end(string code) =>
        Assert.Equal((false, FailedAt.AddDays(7)), Policy.AfterDecline(1, code, FailedAt, FailedAt));

    [Fact]
    public void A_decline_after_the_window_drops_the_charge_whatever_the_attempt() =>
        Assert.True(Policy.AfterDecline(2, "insufficient_funds", FailedAt, FailedAt.AddDays(7).AddSeconds(1)).Drop);

    [Fact]
    public void The_fifth_attempt_is_the_last() =>
        Assert.True(Policy.AfterDecline(5, "insufficient_funds", FailedAt, FailedAt.AddDays(2)).Drop);

    [Theory]
    [InlineData("insufficient_funds", DeclineKind.Soft)]
    [InlineData("try_again_later", DeclineKind.Soft)]
    [InlineData("expired_card", DeclineKind.Hard)]
    [InlineData("authentication_required", DeclineKind.AuthenticationRequired)]
    public void Declines_are_read_by_what_they_ask_for(string code, DeclineKind kind) =>
        Assert.Equal(kind, DunningPolicy.Classify(code));
}
