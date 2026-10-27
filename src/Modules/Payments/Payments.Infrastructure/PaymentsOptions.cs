namespace Payments.Infrastructure;

/// <summary>Configuration section <c>Payments</c>.</summary>
public sealed class PaymentsOptions
{
    /// <summary>The secret the PSP signs its webhooks with.</summary>
    public string WebhookSecret { get; set; } = "";
}
