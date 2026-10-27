using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrowdFunding.Messaging;

/// <summary>The broker as a transport of the platform's service host (lesson s5-l01).</summary>
public static class BrokerHosting
{
    /// <summary>
    /// With an <c>Events:Broker</c> section, the events whose consumers are routed to the broker go
    /// through SNS and SQS, and the host gains <c>replay-events</c>; without one, every event stays in process.
    /// </summary>
    public static void IfConfigured(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetSection("Events:Broker").Exists())
        {
            return;
        }

        services.AddBrokerEvents(configuration);
        services.AddServiceCommand(new ServiceCommand("replay-events",
            (provider, args, ct) => EventReplay.Run(provider, args, Console.Out, ct)));
    }
}
