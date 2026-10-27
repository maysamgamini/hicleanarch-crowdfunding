
using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Identity;

using Campaigns.Infrastructure;

using CrowdFunding.Messaging;

using Payments.Client;

using Pledges.Infrastructure;
using Pledges.Infrastructure.FundingCancel;

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

// Notifications runs only in its own service (lesson s5-l04): this program refuses to start while an
// outbox row here still owes it an in-process delivery, which no host could make any more.
// `open-sinks --module <Module>` says whether any is left before a module leaves (s5-l03).
builder.Services.AddOpenSinks();
builder.Services.AddServiceCommand(new ServiceCommand("open-sinks", (services, rest, ct) => OpenSinks.Run(services, rest, Console.Out, ct)));
builder.Services.AddOpenSinksCheck("Notifications");

// Campaigns runs here (Modules:Campaigns:Mode = InProcess) or only in its own service (Remote, lesson
// s5-l08), once `open-sinks --module Campaigns` reports no row here owes it an in-process delivery; Remote
// refuses to start while one does. Rollback: InProcess.
var campaignsHere = builder.Configuration.GetValue("Modules:Campaigns:Mode", "InProcess") switch
{
    "InProcess" => true,
    "Remote" => false,
    var mode => throw new InvalidOperationException($"Modules:Campaigns:Mode is InProcess or Remote, not {mode}."),
};
if (!campaignsHere)
{
    builder.Services.AddOpenSinksCheck("Campaigns");
}

// `check-creators` (lesson s5-l05) exits 0 once no open funding window lacks its creator; `record-creators`
// publishes every LIVE campaign's creator, from Campaigns, so only where Campaigns runs.
builder.Services.AddServiceCommand(new ServiceCommand("check-creators", (services, _, ct) => services.CheckCreators(ct)));
if (campaignsHere)
{
    builder.Services.AddServiceCommand(new ServiceCommand("record-creators", async (services, _, ct) =>
    {
        await services.RecordCreators(ct);
        return 0;
    }));
}

// Each module brings its own services and endpoints. The host only composes them.
if (campaignsHere)
{
    builder.Services.AddCampaignsModule(builder.Configuration);
}

builder.Services.AddPledgesModule(builder.Configuration);
// Payments runs only in its own service (s4-l10): this program sends it StartCardSetup over HTTP and
// exchanges events with it through the broker, and knows nothing of it but its Contracts.
builder.Services.AddPaymentsClient(builder.Configuration);

var app = builder.Build();

app.UseServiceDefaults();
if (campaignsHere)
{
    app.MapCampaignsEndpoints();
}

app.MapPledgesEndpoints();

// The API's description, generated from the endpoints, for a developer's tools and the tests; not published in production.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi().AllowAnonymous();
}

// `migrate` prepares the database and exits, a deploy step, never part of start-up; `replay-events
// --module Pledges --from … --to …` publishes a range of published events again; the other commands
// above run likewise and exit; otherwise it serves.
await app.RunService(args);

/// <summary>Visible to the integration tests, which start the host in memory.</summary>
public partial class Program;
