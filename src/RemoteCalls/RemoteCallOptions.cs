namespace CrowdFunding.RemoteCalls;

/// <summary>
/// How long a call to another service may take. The attempts and back-off must fit inside the total,
/// or the total timeout cuts the last attempt short and the configured retries are a fiction.
/// </summary>
public sealed record RemoteCallOptions
{
    /// <summary>One attempt, from send to the last byte of the reply.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The whole call, every attempt and wait included: what a backer waiting on a page can bear.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Retries after the first attempt. With 2 s attempts, one retry is all that fits in 5 s.</summary>
    public int MaxRetries { get; init; } = 1;

    /// <summary>The wait before the first retry; it doubles each time, with jitter of at most the same amount.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The worst case: every attempt runs to its timeout and every wait gets its full jitter.</summary>
    public TimeSpan WorstCase =>
        (AttemptTimeout * (MaxRetries + 1)) + TimeSpan.FromTicks(Enumerable.Range(0, MaxRetries).Sum(i => BaseDelay.Ticks * (1L << i) * 2));

    /// <summary>Refuses settings whose worst case would not fit in the total timeout.</summary>
    public void Validate()
    {
        if (WorstCase > TotalTimeout)
        {
            throw new InvalidOperationException(
                $"{MaxRetries + 1} attempts of {AttemptTimeout.TotalSeconds:0.##} s with back-off take up to {WorstCase.TotalSeconds:0.##} s, more than the {TotalTimeout.TotalSeconds:0.##} s total.");
        }
    }
}
