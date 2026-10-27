using BuildingBlocks.Infrastructure.Hosting;

using CrowdFunding.Messaging;

#if (single)
using Sample;
#else
using Sample.Infrastructure;
#endif

// The container's health check: ask the running program's readiness, and exit.
if (args.FirstOrDefault() == HealthProbe.Command)
{
    Environment.ExitCode = await HealthProbe.Run(HealthProbe.Ready());
    return;
}

var builder = WebApplication.CreateBuilder(args);

// The platform's plumbing under this service's own name, the broker when Events:Broker is set, and one module.
builder.AddServiceDefaults("crowdfunding-sample", BrokerHosting.IfConfigured);
builder.Services.AddSampleModule(builder.Configuration);

var app = builder.Build();

app.UseServiceDefaults();
app.MapSampleEndpoints();

// The deploy steps and commands, which run and exit: `hand-over` once, as the owner, gives this module's
// schema to the service's migration role; `migrate` every release, as that role (or as the owner, locally);
// `replay-events` with the broker. Otherwise it serves.
await app.RunService(args);

/// <summary>Visible to the service's tests, which start this host in memory.</summary>
public partial class Program;
