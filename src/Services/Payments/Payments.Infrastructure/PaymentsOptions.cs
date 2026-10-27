namespace Payments.Infrastructure;

/// <summary>Configuration section <c>Payments</c>.</summary>
public sealed class PaymentsOptions
{
    /// <summary>The secret the PSP signs its webhooks with.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>
    /// The secret before the current one, accepted too while a new secret is rolled out: set it to the
    /// old value with the new <see cref="WebhookSecret"/>, and remove it once the PSP signs with the new one only.
    /// </summary>
    public string? PreviousWebhookSecret { get; set; }

    public IReadOnlyCollection<string> WebhookSecrets =>
        string.IsNullOrEmpty(PreviousWebhookSecret) ? [WebhookSecret] : [WebhookSecret, PreviousWebhookSecret];
}
