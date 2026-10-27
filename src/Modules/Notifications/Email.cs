using System.Collections.Concurrent;

namespace Notifications;

/// <summary>One email to one person: their user id, and the address it was looked up to.</summary>
public sealed record Email(Guid RecipientId, string Address, string Subject, string Body);

/// <summary>Where emails go: SES in AWS, a list in memory on a laptop and in tests.</summary>
public interface IEmailSender
{
    Task Send(Email email, CancellationToken cancellationToken);
}

/// <summary>
/// Where a user's email address is found: the Cognito user pool in AWS, a made-up address on a
/// laptop. Null: the user no longer exists, and there is nobody to tell.
/// </summary>
public interface IUserDirectory
{
    Task<string?> EmailOf(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Keeps every email it is given, for a developer to read and a test to count.</summary>
public sealed class InMemoryEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<Email> _sent = new();

    public IReadOnlyCollection<Email> Sent => _sent;

    public Task Send(Email email, CancellationToken cancellationToken)
    {
        _sent.Enqueue(email);
        return Task.CompletedTask;
    }
}

/// <summary>Every user has an address, made from their id, which no mail server accepts.</summary>
public sealed class InMemoryUserDirectory : IUserDirectory
{
    public Task<string?> EmailOf(Guid userId, CancellationToken cancellationToken) => Task.FromResult<string?>($"{userId:N}@example.invalid");
}
