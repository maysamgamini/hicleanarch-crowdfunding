using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The API speaks snake_case JSON: goal_minor, campaign_id.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddProblemDetails();
builder.Services.AddDispatcher();
builder.Services.AddSingleton<DatabaseMigrator>();

// Each module brings its own services and endpoints. The host only composes them.
builder.Services.AddCampaignsModule(builder.Configuration);

var app = builder.Build();

// `dotnet run -- migrate` prepares the database and exits. It is a deploy step, never part of start-up.
if (args.Contains("migrate"))
{
    var owner = app.Configuration.GetConnectionString("Owner")
        ?? throw new InvalidOperationException("ConnectionStrings:Owner is not set.");
    await app.Services.GetRequiredService<DatabaseMigrator>().Run(owner, CancellationToken.None);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapCampaignsEndpoints();

await app.RunAsync();

/// <summary>Visible to the integration tests, which start the host in memory.</summary>
public partial class Program;
