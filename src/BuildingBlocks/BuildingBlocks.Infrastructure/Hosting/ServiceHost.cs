using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Caching;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// A transport for events between services, wired by the platform when its settings are present: the
/// broker (CrowdFunding.Messaging's <c>BrokerHosting.IfConfigured</c>) is the one the course uses.
/// </summary>
public delegate void EventTransport(IServiceCollection services, IConfiguration configuration);

/// <summary>A one-off command a host runs instead of serving, such as <c>replay-events</c>; it returns the exit code.</summary>
public sealed record ServiceCommand(string Name, Func<IServiceProvider, IReadOnlyList<string>, CancellationToken, Task<int>> Run);

/// <summary>
/// The plumbing every service host shares, wired in one place so that each host is the same (lesson
/// s5-l01): the snake_case API, problem replies (a database that cannot be reached is a 503), the
/// dispatcher, the migrator, traces, metrics and trace-correlated JSON logs under the service's own
/// name, both authentication schemes (users' tokens, and service tokens for internal endpoints), the
/// outbox with in-process delivery and, given a transport, the broker, the proxy headers, the graceful
/// stop, the read cache and the health checks. A host adds its one module and its endpoints, and
/// nothing else. No domain type belongs here: the platform knows no module.
/// </summary>
public static class ServiceHost
{
    /// <summary>
    /// Registers the shared plumbing; <paramref name="serviceName"/> names the service in traces and
    /// metrics. Each transport adds itself when its settings are present.
    /// </summary>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, string serviceName, params EventTransport[] transports)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(transports);

        // One JSON object per line, with the trace's ids in the first scope.
        builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

        var services = builder.Services;
        var configuration = builder.Configuration;
        RetiredSettings.Refuse(configuration);
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        services.AddProblemDetails();
        services.AddExceptionHandler<DatabaseUnavailableHandler>();
        services.AddDispatcher();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DatabaseMigrator>();
        services.AddServiceTelemetry(serviceName, exportOtlp: !string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]));
        services.AddTokenAuthentication(configuration);
        services.AddServiceTokenAuthentication(configuration);
        services.AddInProcessEvents();
        services.AddProxyHeaders(configuration);
        services.AddGracefulStop();
        services.AddQueryCache(configuration);
        services.AddHealth(configuration);
        foreach (var transport in transports)
        {
            transport(services, configuration);
        }

        return builder;
    }

    /// <summary>A command <see cref="RunService"/> runs, by name, instead of serving.</summary>
    public static IServiceCollection AddServiceCommand(this IServiceCollection services, ServiceCommand command) =>
        services.AddSingleton(command);

    /// <summary>The shared request pipeline and the health endpoints; the host maps its module's endpoints after it.</summary>
    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        app.UseGatewayClientAddress();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealth();
        return app;
    }

    /// <summary>
    /// Runs one deploy step or command and returns, or serves until stopped:
    /// <list type="bullet">
    /// <item><c>hand-over</c>, once, as the owner (<c>ConnectionStrings:Owner</c>): prepares this
    /// host's schemas as the monolith's migrate step does, then hands each schema and every object in
    /// it to the service's migration role (<c>ConnectionStrings:Migrator</c>).</item>
    /// <item><c>migrate</c>, every release: as the migration role when <c>ConnectionStrings:Migrator</c>
    /// is set, otherwise as the owner, as in the monolith.</item>
    /// <item>a registered <see cref="ServiceCommand"/> (such as <c>replay-events</c>), by its name.</item>
    /// </list>
    /// </summary>
    public static async Task RunService(this WebApplication app, string[] args)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Contains("hand-over"))
        {
            await HandOver(app.Services, app.Configuration, CancellationToken.None);
            return;
        }

        if (args.Contains("migrate"))
        {
            await Migrate(app.Services, app.Configuration, CancellationToken.None);
            return;
        }

        if (args.Length > 0 && app.Services.GetServices<ServiceCommand>().FirstOrDefault(c => c.Name == args[0]) is { } command)
        {
            Environment.ExitCode = await command.Run(app.Services, [.. args.Skip(1)], CancellationToken.None);
            return;
        }

        await app.RunAsync();
    }

    /// <summary>The <c>hand-over</c> step: see <see cref="RunService"/>.</summary>
    public static async Task HandOver(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var owner = Required(configuration, "Owner");
        var migrator = Required(configuration, "Migrator");
        var database = services.GetRequiredService<DatabaseMigrator>();
        var pending = new List<string>();
        foreach (var schema in database.Schemas)
        {
            if (!await SchemaHandover.IsHandedOver(owner, schema, migrator, cancellationToken))
            {
                pending.Add(schema);
            }
        }

        // Once handed over, a schema is the migration role's: the owner no longer migrates it.
        if (pending.Count == 0)
        {
            return;
        }

        await database.Run(owner, cancellationToken);
        foreach (var schema in pending)
        {
            await SchemaHandover.Run(owner, schema, migrator, cancellationToken);
        }
    }

    /// <summary>The <c>migrate</c> step: see <see cref="RunService"/>.</summary>
    public static async Task Migrate(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var database = services.GetRequiredService<DatabaseMigrator>();
        var migrator = configuration.GetConnectionString("Migrator");
        if (string.IsNullOrEmpty(migrator))
        {
            await database.Run(Required(configuration, "Owner"), cancellationToken);
        }
        else
        {
            await database.RunAsSchemaOwner(migrator, cancellationToken);
        }
    }

    private static string Required(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) ?? throw new InvalidOperationException($"ConnectionStrings:{name} is not set.");

    /// <summary>
    /// OpenTelemetry: a collector beside the program receives OTLP and sends traces to X-Ray and
    /// metrics to CloudWatch. Every source and meter of ours is taken by its prefix, CrowdFunding.*.
    /// Without <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> nothing is sent.
    /// </summary>
    private static IServiceCollection AddServiceTelemetry(this IServiceCollection services, string serviceName, bool exportOtlp)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource("CrowdFunding.*"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("CrowdFunding.*"));

        if (exportOtlp)
        {
            otel.UseOtlpExporter();
        }

        return services;
    }
}
