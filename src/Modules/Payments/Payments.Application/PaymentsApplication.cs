using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;

namespace Payments.Application;

public static class PaymentsApplication
{
    public static IServiceCollection AddPaymentsApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<StartCardSetup, CardSetupStarted>, StartCardSetupHandler>()
            .AddScoped<ICommandHandler<HandlePspWebhook, WebhookHandled>, HandlePspWebhookHandler>();
}
