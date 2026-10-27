using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// Two checks with two audiences. <c>/health/live</c> is the load balancer's: it says only that this
/// process answers, because ECS replaces a task whose target-group check fails, and a check that
/// tested the database would fail on every task at once during a failover. <c>/health/ready</c>
/// tests each module's database and is for dashboards and the deploy's smoke test only.
/// </summary>
public static class HealthEndpoints
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));

        // The first sandbox stacks pointed the load balancer at /health; it stays the live check.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", async (IEnumerable<ModuleDatabase> databases, CancellationToken ct) =>
        {
            var checks = await Task.WhenAll(databases.Select(d => Check(d, ct)));
            var ready = checks.All(c => c.Ok);
            return Results.Json(
                new { status = ready ? "ready" : "not_ready", checks = checks.Select(c => new { name = c.Name, ok = c.Ok, error = c.Error }) },
                statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        return app;
    }

    private static async Task<(string Name, bool Ok, string? Error)> Check(ModuleDatabase database, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(timeout.Token);
            return ($"database:{database.Module}", true, null);
        }
        catch (Exception e) when (e is NpgsqlException or OperationCanceledException or TimeoutException)
        {
            return ($"database:{database.Module}", false, e.GetType().Name);
        }
    }
}
