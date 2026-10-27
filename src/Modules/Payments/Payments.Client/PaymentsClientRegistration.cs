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
    /// With <c>Modules:Payments:Calls = Http</c>, the commands this program sends to Payments go over HTTP
    /// to <c>Modules:Payments:BaseAddress</c> (Payments' internal port). Call after the Payments module, if
    /// it runs here: the last registration of a handler is the one the dispatcher uses. Configuration
    /// section <c>ServiceIdentity</c> names this program's app client (s4-l04).
    /// </summary>
    public static IServiceCollection AddPaymentsClient(this IServiceCollection services, IConfiguration configuration)
    {
        if (!string.Equals(configuration["Modules:Payments:Calls"], "Http", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        var baseAddress = new Uri(configuration["Modules:Payments:BaseAddress"] ?? "http://localhost:8081");
        // Every attempt carries this program's service token for Payments' commands (s4-l04): inside the
        // resilience pipeline, so a retry after the token's expiry gets a fresh one.
        services.AddServiceIdentity(configuration);
        services.AddRemoteServiceClient<RemoteStartCardSetup>(baseAddress).AddServiceToken(PaymentsInternalEndpoints.CommandsScope);
        services.AddScoped<ICommandHandler<StartCardSetup, CardSetupStarted>>(sp => sp.GetRequiredService<RemoteStartCardSetup>());
        return services;
    }
}
