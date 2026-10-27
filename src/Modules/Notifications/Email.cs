using System.Collections.Concurrent;

namespace Notifications;

/// <summary>One email to one person, who is known to us only by their user id.</summary>
public sealed record Email(Guid RecipientId, string Subject, string Body);

/// <summary>Where emails go: SES in AWS, a list in memory on a laptop and in tests.</summary>
public interface IEmailSender
{
    Task Send(Email email, CancellationToken cancellationToken);
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
