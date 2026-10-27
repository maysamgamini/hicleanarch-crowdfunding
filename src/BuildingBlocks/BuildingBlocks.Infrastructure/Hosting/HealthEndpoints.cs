using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Valkey;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

using StackExchange.Redis;

namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// Two checks with two audiences, on ASP.NET Core's health checks. <c>/health/live</c> is the load
/// balancer's: it runs only checks tagged <see cref="Live"/>, of which there are none that leave the
/// process, because ECS replaces a task whose target-group check fails, and a check that tested the
/// database would fail on every task at once during a failover. <c>/health/ready</c> runs the checks
/// tagged <see cref="Ready"/>: each module role's database, and Valkey when it is configured. It is for
/// dashboards and the deploy's smoke test. Both are public, so their body says only ok or not; which
/// check failed, and why, is in the log, never in a reply anyone can read.
/// </summary>
public static class HealthEndpoints
{
    public const string Live = "live";
    public const string Ready = "ready";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);

    public static IServiceCollection AddHealth(this IServiceCollection services, IConfiguration configuration)
    {
        var checks = services.AddHealthChecks();

        // One check per module role, from the roles the modules register, so a new module needs no line here.
        services.AddOptions<HealthCheckServiceOptions>().Configure<IEnumerable<ModuleDatabase>>((options, databases) =>
        {
            foreach (var database in databases)
            {
                options.Registrations.Add(new HealthCheckRegistration(
                    $"database:{database.Module}", _ => new DatabaseCheck(database), HealthStatus.Unhealthy, [Ready], CheckTimeout));
            }
        });

        if (configuration.HasValkey())
        {
            checks.Add(new HealthCheckRegistration(
                "valkey", sp => new ValkeyCheck(sp.GetRequiredService<IConnectionMultiplexer>()), HealthStatus.Unhealthy, [Ready], CheckTimeout));
        }

        return services;
    }

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        var live = Options(Live, healthy: "ok", unhealthy: "not_ok");
        app.MapHealthChecks("/health/live", live).AllowAnonymous();

        // The first sandbox stacks pointed the load balancer at /health; it stays the live check.
        app.MapHealthChecks("/health", live).AllowAnonymous();

        app.MapHealthChecks("/health/ready", Options(Ready, healthy: "ready", unhealthy: "not_ready")).AllowAnonymous();
        return app;
    }

    private static HealthCheckOptions Options(string tag, string healthy, string unhealthy) => new()
    {
        Predicate = check => check.Tags.Contains(tag),
        ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(
            new { status = report.Status == HealthStatus.Unhealthy ? unhealthy : healthy }),
    };

    private sealed class DatabaseCheck(ModuleDatabase database) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
    }

    private sealed class ValkeyCheck(IConnectionMultiplexer valkey) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await valkey.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
    }
}
