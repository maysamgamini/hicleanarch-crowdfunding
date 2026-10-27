
using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Identity;

using CrowdFunding.Pledges.Host;
using CrowdFunding.Messaging;

using Payments.Client;

using Pledges.Infrastructure;
using Pledges.Infrastructure.FundingCancel;

// The container's health check: ask the running program's readiness, and exit (as every service host does).
if (args.FirstOrDefault() == HealthProbe.Command)
{
    Environment.ExitCode = await HealthProbe.Run(HealthProbe.Ready());
    return;
}

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

// The Pledges service (lesson s5-l10): the old monolith's host, which runs Pledges alone. The platform's
// plumbing every service shares, under its own name, with the broker when Events:Broker is set.
builder.AddServiceDefaults("crowdfunding-pledges", BrokerHosting.IfConfigured);
builder.Services.AddOpenApi();

// Notifications runs only in its own service (lesson s5-l04): this program refuses to start while an
// outbox row here still owes it an in-process delivery, which no host could make any more.
// `open-sinks --module <Module>` says whether any is left before a module leaves (s5-l03).
builder.Services.AddOpenSinks();
builder.Services.AddServiceCommand(new ServiceCommand("open-sinks", (services, rest, ct) => OpenSinks.Run(services, rest, Console.Out, ct)));
builder.Services.AddOpenSinksCheck("Notifications");

// What s4-l07's move left of Payments on the shared cluster leaves a week after the fence (CQA-2102):
// `drop-moved-payments`, as the shared cluster's owner. No deploy runs it since s5-l10 (CQA-2117): Pledges'
// migrate step never has the owner's secret, and a sandbox rebuilt since then has nothing of the move. An
// environment kept up since s4-l07 runs it once, in Pledges' hand-over task: hand-over.sh pledges drop-moved-payments.
builder.Services.AddServiceCommand(new ServiceCommand(MovedPayments.Command, (services, _, ct) =>
    MovedPayments.Run(services.GetRequiredService<IConfiguration>().GetConnectionString("Owner")
        ?? throw new InvalidOperationException("ConnectionStrings:Owner is not set."), Console.Out, DateTimeOffset.UtcNow, ct)));

// Campaigns runs only in its own service (lesson s5-l09): as for Notifications, this program refuses to start
// while a row here still owes it an in-process delivery.
builder.Services.AddOpenSinksCheck("Campaigns");

// `check-creators` (lesson s5-l05) exits 0 once no open funding window lacks its creator.
builder.Services.AddServiceCommand(new ServiceCommand("check-creators", (services, _, ct) => services.CheckCreators(ct)));

// Its one module, Pledges, and of Payments only its client.
builder.Services.AddPledgesModule(builder.Configuration);
// Payments runs only in its own service (s4-l10): this program sends it StartCardSetup over HTTP and
// exchanges events with it through the broker, and knows nothing of it but its Contracts.
builder.Services.AddPaymentsClient(builder.Configuration);

var app = builder.Build();

app.UseServiceDefaults();
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

/// <summary>Visible to the integration tests, which start the Pledges service in memory.</summary>
public partial class Program;
