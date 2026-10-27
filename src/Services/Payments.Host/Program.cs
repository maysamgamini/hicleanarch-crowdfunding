using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Payments.Infrastructure;

namespace Payments.Host;

/// <summary>
/// The Payments service: the same module the monolith runs, composed alone. Until its events and its
/// command can cross a process boundary (s4-l02, s4-l03), it refuses to start: Pledges, Campaigns and
/// Notifications consume its events and do not run here.
/// </summary>
public static class PaymentsProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // One JSON object per line, each carrying its trace's ids, as the monolith logs.
        builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<DatabaseUnavailableHandler>();
        builder.Services.AddDispatcher();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DatabaseMigrator>();
        builder.Services.AddTokenAuthentication(builder.Configuration);
        builder.Services.AddInProcessEvents();
        builder.Services.AddProxyHeaders(builder.Configuration);
        builder.Services.AddGracefulStop();
        builder.Services.AddHealth(builder.Configuration);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("crowdfunding-payments"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(OutboxDispatcher.ActivitySourceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("CrowdFunding.*"));
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        builder.Services.AddPaymentsModule(builder.Configuration);

        var app = builder.Build();

        // `migrate` prepares Payments' schema and exits, as the monolith's does for every module.
        if (args.Contains("migrate"))
        {
            var owner = app.Configuration.GetConnectionString("Owner")
                ?? throw new InvalidOperationException("ConnectionStrings:Owner is not set.");
            await app.Services.GetRequiredService<DatabaseMigrator>().Run(owner, CancellationToken.None);
            return;
        }

        app.UseForwardedHeaders();
        app.UseGatewayClientAddress();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealth();
        app.MapPaymentsEndpoints();

        await app.RunAsync();
    }
}
