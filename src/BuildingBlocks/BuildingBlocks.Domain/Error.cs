namespace BuildingBlocks.Domain;

/// <summary>Why an operation was refused. <see cref="Code"/> is stable and meant for clients.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
}

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
}
