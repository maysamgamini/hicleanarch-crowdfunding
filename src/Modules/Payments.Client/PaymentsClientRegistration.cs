using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Identity;

using CrowdFunding.RemoteCalls;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;

namespace Payments.Client;

public static class PaymentsClientRegistration
{
    /// <summary>
    /// The commands this program sends to Payments go over HTTP to Payments' service, at
    /// <c>Services:Payments:BaseAddress</c> (see <see cref="PaymentsPlacement"/>). Configuration section
    /// <c>ServiceIdentity</c> names this program's app client (s4-l04).
    /// </summary>
    public static IServiceCollection AddPaymentsClient(this IServiceCollection services, IConfiguration configuration)
    {
        var placement = PaymentsPlacement.From(configuration);

        // Every attempt carries this program's service token for Payments' commands (s4-l04): inside the
        // resilience pipeline, so a retry after the token's expiry gets a fresh one.
        services.AddServiceIdentity(configuration);
        services.AddRemoteServiceClient<RemoteStartCardSetup>(placement.BaseAddress, placement.Client).AddServiceToken(PaymentsInternalEndpoints.CommandsScope);
        services.AddScoped<ICommandHandler<StartCardSetup, CardSetupStarted>>(sp => sp.GetRequiredService<RemoteStartCardSetup>());
        return services;
    }
}
