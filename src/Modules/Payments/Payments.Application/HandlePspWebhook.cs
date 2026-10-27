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

            default:
                return Result.Success(WebhookHandled.Ignored);
        }
    }
}
