using BuildingBlocks.Application;

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
    /// it runs here: the last registration of a handler is the one the dispatcher uses.
    /// </summary>
    public static IServiceCollection AddPaymentsClient(this IServiceCollection services, IConfiguration configuration)
    {
        if (!string.Equals(configuration["Modules:Payments:Calls"], "Http", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        var baseAddress = new Uri(configuration["Modules:Payments:BaseAddress"] ?? "http://localhost:8081");
        services.AddRemoteServiceClient<RemoteStartCardSetup>(baseAddress);
        services.AddScoped<ICommandHandler<StartCardSetup, CardSetupStarted>>(sp => sp.GetRequiredService<RemoteStartCardSetup>());
        return services;
    }
}
