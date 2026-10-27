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
}

/// <summary>
/// Sends through Amazon SES. We know people by their Cognito user id (<c>sub</c>), so the address is
/// looked up in the user pool first. A new SES account is in its sandbox: it sends only to verified
/// addresses until production access is granted.
/// </summary>
internal sealed class SesEmailSender(IAmazonSimpleEmailServiceV2 ses, IAmazonCognitoIdentityProvider cognito, IOptions<NotificationsOptions> options) : IEmailSender
{
    public async Task Send(Email email, CancellationToken cancellationToken)
    {
        var users = await cognito.ListUsersAsync(new ListUsersRequest
        {
            UserPoolId = options.Value.UserPoolId,
            Filter = $"sub = \"{email.RecipientId}\"",
            Limit = 1,
        }, cancellationToken);
        var address = users.Users?.FirstOrDefault()?.Attributes?.FirstOrDefault(a => a.Name == "email")?.Value;
        if (address is null)
        {
            return; // the user no longer exists: nobody to tell
        }

        await ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = options.Value.FromAddress,
            Destination = new Destination { ToAddresses = [address] },
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
}
