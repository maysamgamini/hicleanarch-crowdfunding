namespace BuildingBlocks.Domain;

/// <summary>
/// The outcome of an operation that can be refused: a value, or the errors that explain why not.
/// A refusal is an expected answer, not an exception.
/// </summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Errors = [];
    }

    private Result(IReadOnlyList<Error> errors) => Errors = errors;

    public bool IsSuccess => Errors.Count == 0;

    public IReadOnlyList<Error> Errors { get; }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("A refused result has no value.");

    internal static Result<T> Success(T value) => new(value);

    internal static Result<T> Failure(IReadOnlyList<Error> errors) =>
        errors.Count > 0 ? new(errors) : throw new ArgumentException("A refusal needs at least one error.", nameof(errors));
}

public static class Result
{
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure([error]);

    public static Result<T> Failure<T>(IReadOnlyList<Error> errors) => Result<T>.Failure(errors);
}
