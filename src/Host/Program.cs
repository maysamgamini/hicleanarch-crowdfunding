using System.Text.Json;

using BuildingBlocks.Application;

using Campaigns.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The API speaks snake_case JSON: goal_minor, campaign_id.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddProblemDetails();
builder.Services.AddDispatcher();

// Each module brings its own services and endpoints. The host only composes them.
builder.Services.AddCampaignsModule();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapCampaignsEndpoints();

await app.RunAsync();

/// <summary>Visible to the integration tests, which start the host in memory.</summary>
public partial class Program;
