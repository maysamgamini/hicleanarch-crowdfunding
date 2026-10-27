using BuildingBlocks.Infrastructure.Events;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CrowdFunding.Host;

/// <summary>
/// Traces and metrics in the vendor-neutral OpenTelemetry format. On AWS a collector beside the
/// program receives them over OTLP and sends them to X-Ray and CloudWatch; locally nothing is exported.
/// </summary>
internal static class Telemetry
{
    public const string ServiceName = "crowdfunding-api";

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = http => http.Request.Path != "/health")
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(OutboxDispatcher.MeterName));

        // The collector's address comes from the standard variable; without it, nothing is sent.
        if (!string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return services;
    }
}
