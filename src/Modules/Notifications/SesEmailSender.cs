using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;

using Microsoft.Extensions.Options;

namespace Notifications;

/// <summary>Configuration section <c>Notifications</c>.</summary>
public sealed class NotificationsOptions
{
    /// <summary><c>InMemory</c> (the default) or <c>Ses</c>.</summary>
    public string Sender { get; set; } = "InMemory";

    public string FromAddress { get; set; } = "";

    /// <summary>The Cognito user pool where a user id's email address is looked up.</summary>
    public string UserPoolId { get; set; } = "";

    /// <summary>
    /// Emails each task sends per second at most. SES's maximum send rate is a quota of the account
    /// (1 a second in its sandbox; 14 a second, raised on request, in production), shared by every
    /// task: set this to the account's rate divided by the number of tasks.
    /// </summary>
    public double SendsPerSecond { get; set; } = 14;

    /// <summary>Sends one task keeps in flight at once: with SES answering in about 50 ms, 10 keep up with 200 a second.</summary>
    public int MaxConcurrentSends { get; set; } = 10;

    /// <summary>Emails one worker claims at a time. Sent at <see cref="SendsPerSecond"/>, a batch must end well inside <see cref="Lease"/>.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How long the worker waits for new emails when it found none.</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A claimed email no worker has finished is claimed again after this: the task that held it died.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The wait after a recipient's first failed send; it doubles with each further failure, up to an hour.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Sends tried for one recipient before the email is given up, logged and counted.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>How long an address looked up in the user pool is used before it is looked up again.</summary>
    public TimeSpan AddressFreshFor { get; set; } = TimeSpan.FromDays(1);
}

/// <summary>
/// Sends through Amazon SES, to an address already looked up. A new SES account is in its sandbox:
/// it sends only to verified addresses until production access is granted.
/// </summary>
internal sealed class SesEmailSender(IAmazonSimpleEmailServiceV2 ses, IOptions<NotificationsOptions> options) : IEmailSender
{
    public Task Send(Email email, CancellationToken cancellationToken) =>
        ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = options.Value.FromAddress,
            Destination = new Destination { ToAddresses = [email.Address] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = email.Subject },
                    Body = new Body { Text = new Content { Data = email.Body } },
                },
            },
        }, cancellationToken);
}

/// <summary>
/// We know people by their Cognito user id (<c>sub</c>), so their address is in the user pool.
/// <c>ListUsers</c> has a low request quota of its own, which is why <see cref="AddressBook"/> keeps
/// what it finds.
/// </summary>
internal sealed class CognitoUserDirectory(IAmazonCognitoIdentityProvider cognito, IOptions<NotificationsOptions> options) : IUserDirectory
{
    public async Task<string?> EmailOf(Guid userId, CancellationToken cancellationToken)
    {
        var users = await cognito.ListUsersAsync(new ListUsersRequest
        {
            UserPoolId = options.Value.UserPoolId,
            Filter = $"sub = \"{userId}\"",
            Limit = 1,
        }, cancellationToken);
        return users.Users?.FirstOrDefault()?.Attributes?.FirstOrDefault(a => a.Name == "email")?.Value;
    }
}
