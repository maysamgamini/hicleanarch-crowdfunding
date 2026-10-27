namespace BuildingBlocks.Domain;

/// <summary>Why an operation was refused. <see cref="Code"/> is stable and meant for clients.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    /// <summary>For a refusal that will pass later: when to ask again.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);

    /// <summary>Not now: a shared budget has no room, and <paramref name="retryAfter"/> says when it will.</summary>
    public static Error TooManyRequests(string code, string message, TimeSpan retryAfter) =>
        new(ErrorKind.TooManyRequests, code, message) { RetryAfter = retryAfter };
}

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    TooManyRequests,
}
