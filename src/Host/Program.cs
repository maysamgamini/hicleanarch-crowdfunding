
using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Identity;

using Campaigns.Infrastructure;

using CrowdFunding.Messaging;

using Notifications;

using Payments.Client;

using Pledges.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// `dotnet run -- token creator` prints a local token for development: a new user in that group.
// It runs before any service is built, so standard output carries the token and nothing else, and
// TOKEN=$(...) captures it whole. Anywhere but Development it refuses, on standard error.
if (args.FirstOrDefault() == "token")
{
    if (!builder.Environment.IsDevelopment())
    {
        await Console.Error.WriteLineAsync($"The token command runs only in Development; this is {builder.Environment.EnvironmentName}.");
        Environment.ExitCode = 2;
        return;
    }

    var auth = builder.Configuration.GetSection("Auth");
    Console.WriteLine(LocalTokens.Issue(auth["LocalSigningKey"]!, auth["ClientId"]!, Guid.NewGuid(), [.. args.Skip(1)]));
    return;
}

// The platform's plumbing every service shares (lesson s5-l01), under this program's name, with the broker
// when Events:Broker is set; then the read API's description and the modules this program still runs.
builder.AddServiceDefaults("crowdfunding-api", BrokerHosting.IfConfigured);
builder.Services.AddOpenApi();

// Notifications runs here (Modules:Notifications:Mode = InProcess) or only in its own service (Remote,
// lesson s5-l03). Remote refuses to start while an outbox row here still owes Notifications an in-process
// delivery, which only a host that runs it can make; `open-sinks --module Notifications` says when none is left.
var notificationsHere = builder.Configuration.GetValue("Modules:Notifications:Mode", "InProcess") switch
{
    "InProcess" => true,
    "Remote" => false,
    var mode => throw new InvalidOperationException($"Modules:Notifications:Mode is InProcess or Remote, not {mode}."),
};
builder.Services.AddOpenSinks();
builder.Services.AddServiceCommand(new ServiceCommand("open-sinks", (services, rest, ct) => OpenSinks.Run(services, rest, Console.Out, ct)));
if (!notificationsHere)
{
    builder.Services.AddOpenSinksCheck(NotificationsModule.Name);
}

// Each module brings its own services and endpoints. The host only composes them.
builder.Services.AddCampaignsModule(builder.Configuration);
builder.Services.AddPledgesModule(builder.Configuration);
if (notificationsHere)
{
    builder.Services.AddNotificationsModule(builder.Configuration);
}
// Payments runs only in its own service (s4-l10): this program sends it StartCardSetup over HTTP and
// exchanges events with it through the broker, and knows nothing of it but its Contracts.
builder.Services.AddPaymentsClient(builder.Configuration);

var app = builder.Build();

app.UseServiceDefaults();
app.MapCampaignsEndpoints();
app.MapPledgesEndpoints();
if (notificationsHere)
{
    app.MapNotificationsEndpoints();
}

// The API's description, generated from the endpoints, for a developer's tools and the tests; not published in production.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi().AllowAnonymous();
}

// `migrate` prepares the database and exits, a deploy step, never part of start-up; `replay-events
// --module Pledges --from … --to …` publishes a range of published events again; otherwise it serves.
await app.RunService(args);

/// <summary>Visible to the integration tests, which start the host in memory.</summary>
public partial class Program;
