using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Infrastructure;

using CrowdFunding.Host;

using Payments.Infrastructure;

using Pledges.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The API speaks snake_case JSON: goal_minor, campaign_id.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddProblemDetails();
builder.Services.AddDispatcher();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddTelemetry(builder.Configuration);
builder.Services.AddTokenAuthentication(builder.Configuration);
builder.Services.AddInProcessEvents();

// Each module brings its own services and endpoints. The host only composes them.
builder.Services.AddCampaignsModule(builder.Configuration);
builder.Services.AddPledgesModule(builder.Configuration);
builder.Services.AddPaymentsModule(builder.Configuration);

var app = builder.Build();

// `dotnet run -- migrate` prepares the database and exits. It is a deploy step, never part of start-up.
if (args.Contains("migrate"))
{
    var owner = app.Configuration.GetConnectionString("Owner")
        ?? throw new InvalidOperationException("ConnectionStrings:Owner is not set.");
    await app.Services.GetRequiredService<DatabaseMigrator>().Run(owner, CancellationToken.None);
    return;
}

// `dotnet run -- token creator` prints a local token for development: a new user in that group.
if (args.FirstOrDefault() == "token" && app.Environment.IsDevelopment())
{
    var auth = app.Configuration.GetSection("Auth");
    Console.WriteLine(LocalTokens.Issue(auth["LocalSigningKey"]!, auth["ClientId"]!, Guid.NewGuid(), [.. args.Skip(1)]));
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapCampaignsEndpoints();
app.MapPledgesEndpoints();
app.MapPaymentsEndpoints();

await app.RunAsync();

/// <summary>Visible to the integration tests, which start the host in memory.</summary>
public partial class Program;
