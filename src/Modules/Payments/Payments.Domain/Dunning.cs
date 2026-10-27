namespace Payments.Domain;

/// <summary>What a decline says about the next try.</summary>
public enum DeclineKind
{
    /// <summary>It may pass later on its own: insufficient funds, a temporary problem at the bank.</summary>
    Soft,

    /// <summary>
    /// It will not pass with this card, and the issuer or the card network says not to try it again:
    /// expired, lost, stolen, fraudulent, a stopped payment. Only a new card from the backer helps.
    /// </summary>
    Hard,

    /// <summary>The bank wants the cardholder to confirm, on our page, with the bank's check.</summary>
    AuthenticationRequired,
}

/// <summary>
/// Lesson s3-a04's recovery window, as a rule: a failed charge is retried for 7 days, then dropped,
/// and the campaign stays funded. Only a soft decline is retried on its own: on days 1, 3 and 5, and a
/// final attempt at the end of the window. A hard decline, or a bank that wants the cardholder, waits
/// for the backer (a new card, or a confirmation on our page); if the backer does nothing, the charge
/// is dropped at the end of the window without another attempt on that card.
/// </summary>
public sealed record DunningPolicy(TimeSpan Window, IReadOnlyList<TimeSpan> SoftRetries, int MaxAttempts)
{
    /// <summary>The deadline's charge, three retries, and the final one.</summary>
    public static DunningPolicy Default { get; } = new(TimeSpan.FromDays(7), [TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(5)], 5);

    /// <summary>
    /// The decline codes whose next step, in the PSP's own table, is to try again later. Every other
    /// code asks the backer to act (call the bank, use another card), and the card networks limit
    /// how often a declined card may be tried again, so it is not retried on its own.
    /// </summary>
    private static readonly HashSet<string> SoftCodes = new(StringComparer.Ordinal)
    {
        "insufficient_funds", "processing_error", "try_again_later", "issuer_not_available", "reenter_transaction", "approve_with_id",
    };

    /// <summary>
    /// What a decline asks for. The PSP's advice code, when it gives one, decides
    /// (<c>do_not_try_again</c>, <c>try_again_later</c>, <c>confirm_card_data</c>); without one, the decline code does.
    /// </summary>
    public static DeclineKind Classify(string declineCode, string? adviceCode = null) =>
        declineCode == "authentication_required" || adviceCode == "confirm_card_data" ? DeclineKind.AuthenticationRequired
        : adviceCode == "do_not_try_again" ? DeclineKind.Hard
        : adviceCode == "try_again_later" ? DeclineKind.Soft
        : SoftCodes.Contains(declineCode) ? DeclineKind.Soft
        : DeclineKind.Hard;

    /// <summary>
    /// After attempt <paramref name="attemptNo"/> was declined: whether the charge is dropped now, or
    /// when to try again. <paramref name="firstFailedAt"/> is when the window opened (the first decline).
    /// </summary>
    public (bool Drop, DateTimeOffset? NextAttemptAt) AfterDecline(int attemptNo, string declineCode, DateTimeOffset firstFailedAt, DateTimeOffset now) =>
        AfterDecline(attemptNo, declineCode, null, firstFailedAt, now);

    /// <summary>
    /// After attempt <paramref name="attemptNo"/> was declined: whether the charge is dropped now, or
    /// when it is due again. For a decline that waits for the backer, that is the end of the window,
    /// when it is dropped unless the backer has acted (<see cref="RetriesOnItsOwn"/>).
    /// </summary>
    public (bool Drop, DateTimeOffset? NextAttemptAt) AfterDecline(int attemptNo, string declineCode, string? adviceCode, DateTimeOffset firstFailedAt, DateTimeOffset now)
    {
        var windowEnds = firstFailedAt + Window;
        if (attemptNo >= MaxAttempts || now >= windowEnds)
        {
            return (true, null);
        }

        if (!RetriesOnItsOwn(declineCode, adviceCode))
        {
            return (false, windowEnds); // the backer acts, or the charge is dropped at the end
        }

        var next = SoftRetries.Select(after => firstFailedAt + after).Where(at => at > now).DefaultIfEmpty(windowEnds).First();
        return (false, next);
    }

    /// <summary>Whether a charge declined this way is tried again with the same card without the backer.</summary>
    public static bool RetriesOnItsOwn(string declineCode, string? adviceCode) => Classify(declineCode, adviceCode) == DeclineKind.Soft;
}
