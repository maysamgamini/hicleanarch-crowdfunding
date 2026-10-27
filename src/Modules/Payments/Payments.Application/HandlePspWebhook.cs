using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;

namespace Payments.Application;

/// <summary>A PSP message whose signature was already verified. <see cref="Data"/> is its payload.</summary>
public sealed record HandlePspWebhook(string MessageId, string Type, JsonElement Data) : ICommand<WebhookHandled>;

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
    ChargeResults results,
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
            case "charge_succeeded":
            case "charge_failed":
                return Result.Success(await ApplyCharge(command, cancellationToken) ? WebhookHandled.Applied : WebhookHandled.Ignored);

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
            ? new ChargeOutcome.Succeeded(paymentId)
            : new ChargeOutcome.Declined(data.GetProperty("decline_code").GetString()!, paymentId);
        return await results.Apply(found.CampaignId, found.Work, n, outcome, ct);
    }
}
