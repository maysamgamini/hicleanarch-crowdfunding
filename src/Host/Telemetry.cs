using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CrowdFunding.Host;

/// <summary>
/// Traces and metrics in the vendor-neutral OpenTelemetry format. On AWS a collector beside the
/// program receives them over OTLP and sends them to X-Ray and CloudWatch; locally nothing is exported.
/// Logs go to standard output, where the container's log driver ships them to CloudWatch Logs.
/// </summary>
internal static class Telemetry
{
    public const string ServiceName = "crowdfunding-api";

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                // Every span source of ours, by its prefix, as for the meters: the outbox's deliveries, the
                // broker's processing spans and the PSP webhook's own traces.
                .AddSource("CrowdFunding.*"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Every meter of ours, by its prefix: the outbox, the operations gauges, and each module's
                // own counters (such as Pledges' close recheck), without a line per meter to forget.
                .AddMeter("CrowdFunding.*"));

        // The collector's address comes from the standard variable; without it, nothing is sent.
        if (!string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return services;
    }

    /// <summary>
    /// One JSON object per line, so CloudWatch Logs keeps each record as one event and Logs Insights
    /// discovers its fields. The first scope of every record written inside a request or an event
    /// delivery carries that trace's <c>TraceId</c> and <c>SpanId</c>, the ids an X-Ray trace and a
    /// problem reply's <c>traceId</c> show, so an operator finds the lines with
    /// <c>filter Scopes.0.TraceId = "…"</c>.
    /// </summary>
    public static ILoggingBuilder AddTraceCorrelatedConsole(this ILoggingBuilder logging)
    {
        logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        return logging.AddJsonConsole(options => options.IncludeScopes = true);
    }
}
