using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;

namespace Payments.Application;

/// <summary>A PSP message whose signature was already verified. <see cref="Data"/> is its payload.</summary>
public sealed record HandlePspWebhook(string MessageId, string Type, JsonElement Data) : ICommand<WebhookHandled>
{
    /// <summary>
    /// Whether the payload has every field its type needs, of the right kind. One that does not is
    /// refused before anything is recorded; a type we do not handle is acknowledged and ignored.
    /// </summary>
    public static bool IsReadable(string type, JsonElement data) => data.ValueKind == JsonValueKind.Object && type switch
    {
        "setup_succeeded" => Text(data, "setup_id") && Text(data, "payment_method"),
        "setup_failed" => Text(data, "setup_id"),
        "charge_succeeded" => Text(data, "pledge_id") && Text(data, "payment_id") && Number(data, "attempt_no"),
        "charge_failed" => Text(data, "pledge_id") && Text(data, "payment_id") && Number(data, "attempt_no") && Text(data, "decline_code"),
        "payment_succeeded" or "payment_failed" => Text(data, "payment_id"),
        _ => true,
    };

    private static bool Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString());

    private static bool Number(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
}

public enum WebhookHandled
{
    Applied,
    Duplicate,
    Ignored,
}

/// <summary>
/// One transaction per message: the message id goes into the inbox first, and if it was already
/// there, nothing else happens. Every move it makes is conditional, so a message about a state the
/// record has already left is recorded and ignored.
/// </summary>
public sealed class HandlePspWebhookHandler(
    IWebhookInbox inbox,
    ICardSetups setups,
    ISettlements settlements,
    ChargeOutcomes outcomes,
    TimeProvider clock,
    [FromKeyedServices(PaymentsModuleName.Value)] IOutbox outbox) : ICommandHandler<HandlePspWebhook, WebhookHandled>
{
    public async Task<Result<WebhookHandled>> Handle(HandlePspWebhook command, CancellationToken cancellationToken)
    {
        if (!await inbox.TryRecord(command.MessageId, cancellationToken))
        {
            return Result.Success(WebhookHandled.Duplicate);
        }

        switch (command.Type)
        {
            case "setup_succeeded":
                var setup = await setups.TryComplete(
                    command.Data.GetProperty("setup_id").GetString()!,
                    command.Data.GetProperty("payment_method").GetString()!,
                    cancellationToken);
                if (setup is null)
                {
                    return Result.Success(WebhookHandled.Ignored);
                }

                await outbox.Add(new PaymentMethodSaved(
                    Guid.CreateVersion7(), clock.GetUtcNow(), setup.Id, setup.PledgeId, setup.BackerId, setup.PaymentMethodRef!, command.MessageId),
                    cancellationToken);
                return Result.Success(WebhookHandled.Applied);

            case "setup_failed":
                return Result.Success(await setups.TryFail(command.Data.GetProperty("setup_id").GetString()!, cancellationToken)
                    ? WebhookHandled.Applied
                    : WebhookHandled.Ignored);

            // A charge's result, when its reply was lost or the bank decided later. The same conditional
            // move as the resolver's: whichever hears first records it, the other changes nothing.
            case "charge_succeeded" or "charge_failed":
                return Result.Success(await ApplyCharge(command, cancellationToken) ? WebhookHandled.Applied : WebhookHandled.Ignored);

            // The result of a payment the backer confirmed on our page (lesson s3-a04).
            case "payment_succeeded" or "payment_failed":
                var paymentId = command.Data.GetProperty("payment_id").GetString()!;
                var found = await settlements.FindByPayment(paymentId, cancellationToken);
                if (found is not { } payment)
                {
                    return Result.Success(WebhookHandled.Ignored);
                }

                ChargeOutcome outcome = command.Type == "payment_succeeded"
                    ? new ChargeOutcome.Succeeded(paymentId, FeeOf(command.Data))
                    : new ChargeOutcome.Declined(command.Data.TryGetProperty("decline_code", out var code) && code.GetString() is { } c ? c : "authentication_required", paymentId, Advice(command.Data));
                return Result.Success(await Recorded(payment.Charge, payment.AttemptNo, outcome, cancellationToken)
                    ? WebhookHandled.Applied
                    : WebhookHandled.Ignored);

            default:
                return Result.Success(WebhookHandled.Ignored);
        }
    }

    private async Task<bool> ApplyCharge(HandlePspWebhook command, CancellationToken ct)
    {
        var data = command.Data;
        if (!PublicId.TryParse("plg", data.GetProperty("pledge_id").GetString(), out var pledgeId)
            || await settlements.FindCharge(pledgeId, ct) is not { } found)
        {
            return false;
        }

        var paymentId = data.GetProperty("payment_id").GetString()!;
        var n = data.GetProperty("attempt_no").GetInt32();
        ChargeOutcome outcome = command.Type == "charge_succeeded"
            ? new ChargeOutcome.Succeeded(paymentId, FeeOf(data))
            : new ChargeOutcome.Declined(data.GetProperty("decline_code").GetString()!, paymentId, Advice(data));
        return await Recorded(found.Work with { CampaignId = found.CampaignId }, n, outcome, ct);
    }

    // A settlement pass that waits for this charge's look-up days ahead would not see it end until then.
    private async Task<bool> Recorded(ChargeWork charge, int n, ChargeOutcome outcome, CancellationToken ct)
    {
        if (!await outcomes.Record(charge, n, outcome, ct))
        {
            return false;
        }

        await settlements.WakeUp(charge.CampaignId, ct);
        return true;
    }

    private static long? FeeOf(JsonElement data) =>
        data.TryGetProperty("fee_minor", out var fee) && fee.ValueKind == JsonValueKind.Number ? fee.GetInt64() : null;

    private static string? Advice(JsonElement data) =>
        data.TryGetProperty("advice_code", out var advice) && advice.ValueKind == JsonValueKind.String ? advice.GetString() : null;
}
