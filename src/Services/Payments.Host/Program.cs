using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using CrowdFunding.Messaging;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Payments.Infrastructure;
using Payments.Infrastructure.Cutover;

namespace Payments.Host;

/// <summary>
/// The Payments service (s4-l05): the same module the monolith runs, composed alone. Its events reach
/// Pledges, Campaigns and Notifications through the broker, the only route it has to them (without one
/// it refuses to start), and the monolith sends it StartCardSetup on its internal port (8081).
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
                // Every source of ours: the outbox's deliveries and the broker's receive and process spans (s4-l08).
                .AddSource("CrowdFunding.*"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("CrowdFunding.*"));
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        builder.Services.AddPaymentsModule(builder.Configuration);

        // With an Events:Broker section, the events whose consumers are routed to the broker go through SNS and SQS.
        if (builder.Configuration.GetSection("Events:Broker").Exists())
        {
            builder.Services.AddBrokerEvents(builder.Configuration);
        }

        var app = builder.Build();

        // `migrate` prepares Payments' schema and exits, as the monolith's does for every module.
        if (args.Contains("migrate"))
        {
            var owner = app.Configuration.GetConnectionString("Owner")
                ?? throw new InvalidOperationException("ConnectionStrings:Owner is not set.");
            await app.Services.GetRequiredService<DatabaseMigrator>().Run(owner, CancellationToken.None);
            return;
        }

        // `move-payments <step>` runs one step of moving Payments' tables to their own cluster, and exits (s4-l07).
        if (args.FirstOrDefault() == "move-payments")
        {
            Environment.ExitCode = await app.Services.MovePayments(args.ElementAtOrDefault(1), CancellationToken.None);
            return;
        }

        // `replay-events --module Payments --from … --to …` publishes a range of published events again, and exits.
        if (args.FirstOrDefault() == "replay-events")
        {
            Environment.ExitCode = await EventReplay.Run(app.Services, [.. args.Skip(1)], Console.Out, CancellationToken.None);
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
