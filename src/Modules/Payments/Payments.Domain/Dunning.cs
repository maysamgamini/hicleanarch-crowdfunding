namespace Payments.Domain;

/// <summary>What a decline says about the next try.</summary>
public enum DeclineKind
{
    /// <summary>It may pass later on its own: insufficient funds, a temporary problem at the bank.</summary>
    Soft,

    /// <summary>It will not pass with this card: expired, lost, stolen. Only a new card helps.</summary>
    Hard,

    /// <summary>The bank wants the cardholder to confirm, on our page, with the bank's check.</summary>
    AuthenticationRequired,
}

/// <summary>
/// Lesson s3-a04's recovery window, as a rule: a failed charge is retried for 7 days, then dropped,
/// and the campaign stays funded. Soft declines are retried on days 1, 3 and 5; a hard decline or a
/// bank that wants the cardholder waits for the backer; a final attempt comes at the end of the window.
/// </summary>
public sealed record DunningPolicy(TimeSpan Window, IReadOnlyList<TimeSpan> SoftRetries, int MaxAttempts)
{
    /// <summary>The deadline's charge, three retries, and the final one.</summary>
    public static DunningPolicy Default { get; } = new(TimeSpan.FromDays(7), [TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(5)], 5);

    private static readonly HashSet<string> HardCodes = new(StringComparer.Ordinal)
    {
        "expired_card", "lost_card", "stolen_card", "pickup_card", "incorrect_number", "invalid_account", "card_not_supported",
    };

    public static DeclineKind Classify(string declineCode) =>
        declineCode == "authentication_required" ? DeclineKind.AuthenticationRequired
        : HardCodes.Contains(declineCode) ? DeclineKind.Hard
        : DeclineKind.Soft;

    /// <summary>
    /// After attempt <paramref name="attemptNo"/> was declined: whether the charge is dropped now, or
    /// when to try again. <paramref name="firstFailedAt"/> is when the window opened (the first decline).
    /// </summary>
    public (bool Drop, DateTimeOffset? NextAttemptAt) AfterDecline(int attemptNo, string declineCode, DateTimeOffset firstFailedAt, DateTimeOffset now)
    {
        var windowEnds = firstFailedAt + Window;
        if (attemptNo >= MaxAttempts || now >= windowEnds)
        {
            return (true, null);
        }

        if (Classify(declineCode) != DeclineKind.Soft)
        {
            return (false, windowEnds); // the backer acts first, or the final attempt comes at the end
        }

        var next = SoftRetries.Select(after => firstFailedAt + after).Where(at => at > now).DefaultIfEmpty(windowEnds).First();
        return (false, next);
    }
}
