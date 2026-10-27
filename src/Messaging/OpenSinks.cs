using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace CrowdFunding.Messaging;

/// <summary>
/// Outbox rows of this host's modules that still owe <c>module</c> an in-process delivery: rows whose
/// sinks name it, and rows from before sinks existed (s4-l01) of an event type it consumes. Only a host
/// that runs the module can deliver them, so a host must not stop running a module while any is left:
/// the module's new host reads its queue, never another module's outbox.
/// </summary>
public sealed class OpenSinks(IEnumerable<ModuleDatabase> databases, EventSubscriptions subscriptions)
{
    /// <summary>The open rows for <paramref name="module"/>, by producing module of this host; empty when none.</summary>
    public async Task<IReadOnlyDictionary<string, long>> For(string module, CancellationToken cancellationToken)
    {
        var consumed = subscriptions.Events.Where(e => e.Value.Consumers.Contains(module)).Select(e => e.Key).ToArray();
        var open = new Dictionary<string, long>();
        foreach (var database in databases.Where(d => d.Module != module))
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            // The schema is the module's own; the predicate starts with the partial index's own, so the index answers it.
            await using var count = new NpgsqlCommand($"""
                SELECT count(*) FROM "{database.Schema}".outbox
                WHERE {OutboxModel.Undelivered}
                  AND (@module = ANY (sinks) OR (sinks IS NULL AND type = ANY (@types)))
                """, connection);
            count.Parameters.AddWithValue("module", module);
            count.Parameters.AddWithValue("types", consumed);
            var rows = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
            if (rows > 0)
            {
                open[database.Module] = rows;
            }
        }

        return open;
    }

    /// <summary>
    /// <c>open-sinks --module Notifications</c>: prints the open rows and exits 0 when there are none, 1 when
    /// there are. The pipeline runs it before the release that stops running the module.
    /// </summary>
    public static async Task<int> Run(IServiceProvider services, IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        var at = args.ToList().IndexOf("--module");
        if (at < 0 || at + 1 >= args.Count)
        {
            await output.WriteLineAsync("usage: open-sinks --module <Module>");
            return 2;
        }

        var module = args[at + 1];
        var open = await services.GetRequiredService<OpenSinks>().For(module, cancellationToken);
        await output.WriteLineAsync(open.Count == 0
            ? $"No outbox row owes {module} an in-process delivery."
            : $"Outbox rows still owe {module} an in-process delivery: {string.Join(", ", open.Select(o => $"{o.Value} in the {o.Key} outbox"))}.");
        return open.Count == 0 ? 0 : 1;
    }
}

/// <summary>
/// Refuses to start a host that no longer runs a module while rows here still owe that module an in-process
/// delivery. It is the backstop to the deploy's <c>open-sinks</c> step, which runs before the rollout: a host
/// that cannot reach a database at start still starts, not ready, as every host does (s3-l01), and says
/// that it could not check.
/// </summary>
internal sealed partial class OpenSinksCheck(OpenSinks sinks, string module, ILogger<OpenSinks> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, long> open;
        try
        {
            open = await sinks.For(module, cancellationToken);
        }
        catch (NpgsqlException e)
        {
            CouldNotCheck(logger, e, module);
            return;
        }

        if (open.Count > 0)
        {
            throw new InvalidOperationException(
                $"This host no longer runs {module}, but outbox rows still owe it an in-process delivery ({string.Join(", ", open.Select(o => $"{o.Value} in the {o.Key} outbox"))}). " +
                $"Run the previous release, which delivers them, until `open-sinks --module {module}` reports none.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check for outbox rows that still owe {Module} an in-process delivery; the deploy's open-sinks step is the check")]
    private static partial void CouldNotCheck(ILogger logger, Exception exception, string module);
}

public static class OpenSinksRegistration
{
    /// <summary>The <c>open-sinks</c> command's service; every host that may stop running a module registers it.</summary>
    public static IServiceCollection AddOpenSinks(this IServiceCollection services)
    {
        services.TryAddSingleton<OpenSinks>();
        return services;
    }

    /// <summary>
    /// This host no longer runs <paramref name="module"/>: it refuses to start while rows here still owe it an
    /// in-process delivery. Registered before any hosted service that delivers or publishes.
    /// </summary>
    public static IServiceCollection AddOpenSinksCheck(this IServiceCollection services, string module)
    {
        services.AddOpenSinks();
        // Not AddHostedService, which keeps one registration per type: each module left has its own check.
        services.AddSingleton<IHostedService>(sp => new OpenSinksCheck(sp.GetRequiredService<OpenSinks>(), module, sp.GetRequiredService<ILogger<OpenSinks>>()));
        return services;
    }
}
