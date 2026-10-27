using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Caching;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Infrastructure;

using CrowdFunding.Host;

using Notifications;

using Payments.Infrastructure;

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

builder.Logging.AddTraceCorrelatedConsole();

// The API speaks snake_case JSON: goal_minor, campaign_id.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddProblemDetails();
// A database that cannot be reached is a 503 the client may retry, not a 500.
builder.Services.AddExceptionHandler<DatabaseUnavailableHandler>();
builder.Services.AddDispatcher();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddTelemetry(builder.Configuration);
builder.Services.AddTokenAuthentication(builder.Configuration);
builder.Services.AddInProcessEvents();
builder.Services.AddProxyHeaders(builder.Configuration);
builder.Services.AddGracefulStop();
builder.Services.AddQueryCache(builder.Configuration);
builder.Services.AddHealth(builder.Configuration);

// Each module brings its own services and endpoints. The host only composes them.
builder.Services.AddCampaignsModule(builder.Configuration);
builder.Services.AddPledgesModule(builder.Configuration);
builder.Services.AddPaymentsModule(builder.Configuration);
builder.Services.AddNotificationsModule(builder.Configuration);

var app = builder.Build();

// `dotnet run -- migrate` prepares the database and exits. It is a deploy step, never part of start-up.
if (args.Contains("migrate"))
{
    var owner = app.Configuration.GetConnectionString("Owner")
        ?? throw new InvalidOperationException("ConnectionStrings:Owner is not set.");
    await app.Services.GetRequiredService<DatabaseMigrator>().Run(owner, CancellationToken.None);
    return;
}

app.UseForwardedHeaders();
// Behind the gateway, the client is the address the gateway decided (after the forwarded headers, when present).
app.UseGatewayClientAddress();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealth();
app.MapCampaignsEndpoints();
app.MapPledgesEndpoints();
app.MapPaymentsEndpoints();
app.MapNotificationsEndpoints();

await app.RunAsync();

/// <summary>Visible to the integration tests, which start the host in memory.</summary>
public partial class Program;
