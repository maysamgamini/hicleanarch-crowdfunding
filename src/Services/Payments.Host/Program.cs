using BuildingBlocks.Infrastructure.Hosting;

using CrowdFunding.Messaging;

using Payments.Infrastructure;
using Payments.Infrastructure.Cutover;

namespace Payments.Host;

/// <summary>
/// The Payments service (s4-l05): the module alone, on the platform's service host (s5-l01). Its events
/// reach the other services through the broker, the only route it has to them (without one it refuses
/// to start), and Pledges sends it StartCardSetup on its internal port (8081).
/// </summary>
public static class PaymentsProgram
{
    public static async Task Main(string[] args)
    {
        // The container's health check: ask the running program's readiness, and exit (CQA-1518).
        if (args.FirstOrDefault() == HealthProbe.Command)
        {
            Environment.ExitCode = await HealthProbe.Run(HealthProbe.Ready());
            return;
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults("crowdfunding-payments", BrokerHosting.IfConfigured);
        builder.Services.AddPaymentsModule(builder.Configuration);
        // `move-payments <step>` runs one step of moving Payments' tables to their own cluster (s4-l07).
        builder.Services.AddServiceCommand(new ServiceCommand("move-payments",
            (services, rest, ct) => services.MovePayments(rest.Count > 0 ? rest[0] : null, ct)));

        var app = builder.Build();
        app.UseServiceDefaults();
        app.MapPaymentsEndpoints();
        await app.RunService(args);
    }
}
