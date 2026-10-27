using System.Text.Json.Nodes;

using Npgsql;

namespace ContractGate;

/// <summary>An event travels as a message and may outlive its sender; a command or query does not.</summary>
internal enum ContractKind
{
    Event,
    CommandOrQuery,
}

/// <summary>What the producer's outbox says about one event type.</summary>
internal sealed record OutboxFacts(DateTimeOffset? LastWritten, long Owed);

/// <summary>
/// Whether an old event, command or query ("&lt;Producer&gt;.&lt;Name&gt;") may be removed from its contract.
/// Never "after one release": services deploy alone, and a message outlives the code that sent it.
/// It may go only when
/// <list type="number">
/// <item>no running or previous version of any service sends, publishes or reads it;</item>
/// <item>for an event, its producer last wrote it more than 14 days ago, the dead-letter queues' retention
/// (a moved message keeps its original enqueue time), and no outbox row of it still owes a delivery;</item>
/// <item>for an event, events/retired.json lists it, so replay-events refuses to publish it again.</item>
/// </list>
/// </summary>
internal static class Retirement
{
    public static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(14);

    public const string RetiredPath = "events/retired.json";

    public static GateResult Check(
        IVersions repository,
        IReadOnlyDictionary<string, DeployedService> record,
        string type,
        ContractKind kind,
        string currentVersion,
        OutboxFacts? outbox,
        DateTimeOffset now)
    {
        var problems = new List<string>();

        foreach (var deployed in record.Values)
        {
            foreach (var (label, version) in deployed.Versions())
            {
                if (Release.TryLoad(repository, version) is not { } release)
                {
                    problems.Add($"{deployed.Service} ({label}, {version}) predates {Release.ServicesPath}: whether it uses {type} cannot be told");
                    continue;
                }

                var modules = release.Services.GetValueOrDefault(deployed.Service) ?? [];
                foreach (var module in modules)
                {
                    if (release.Expectations.GetValueOrDefault(module) is { } expects && expects.Uses(type))
                    {
                        problems.Add($"{module} in {deployed.Service} ({label}, {release.Short}) still uses {type}");
                    }
                }

                if (modules.Contains(CanIDeploy.ModuleOf(type), StringComparer.Ordinal) && release.Publishes(type))
                {
                    problems.Add($"{CanIDeploy.ModuleOf(type)} in {deployed.Service} ({label}, {release.Short}) still publishes {type}");
                }
            }
        }

        if (kind == ContractKind.CommandOrQuery)
        {
            return new GateResult(problems, ["A command or query: no message of it can be in flight."]);
        }

        if (outbox is null)
        {
            problems.Add($"{type} is an event: the producer's outbox must be read (pass --database)");
        }
        else
        {
            if (outbox.LastWritten is { } last && now - last < DeadLetterRetention)
            {
                problems.Add($"{type} was last written {last:u}; a copy may wait in a dead-letter queue until {last + DeadLetterRetention:u}");
            }

            if (outbox.Owed > 0)
            {
                problems.Add($"{outbox.Owed} outbox rows of {type} still owe a delivery");
            }
        }

        if (!Retired(repository, currentVersion).Contains(type, StringComparer.Ordinal))
        {
            problems.Add($"replay-events could publish {type} again: list it in {RetiredPath}");
        }

        return new GateResult(problems, []);
    }

    /// <summary>The event types replay-events refuses, as "&lt;Producer&gt;.&lt;Name&gt;".</summary>
    public static IReadOnlyList<string> Retired(IVersions repository, string version) =>
        repository.Read(version, RetiredPath) is { } json
            ? [.. JsonNode.Parse(json)!["retired"]!.AsArray().Select(t => (string)t!)]
            : [];

    /// <summary>
    /// The producer's outbox facts for one event type, read with the producer's own login. The outbox is
    /// in the producing module's schema, and its type column holds the event's name.
    /// </summary>
    public static async Task<OutboxFacts> ReadOutbox(string connectionString, string type, CancellationToken cancellationToken)
    {
        var (producer, name) = (CanIDeploy.ModuleOf(type), type.Split('.', 2)[1]);
        if (!producer.All(char.IsAsciiLetter))
        {
            throw new ArgumentException($"{producer} is not a module name.", nameof(type));
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT max(occurred_at), count(*) FILTER (WHERE
                (delivered_at IS NULL AND (sinks IS NULL OR cardinality(array_remove(sinks, 'broker')) > 0))
                OR (published_at IS NULL AND 'broker' = ANY (sinks)))
            FROM "{producer.ToLowerInvariant()}".outbox WHERE type = @type
            """, connection);
        command.Parameters.AddWithValue("type", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new OutboxFacts(reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt64(1));
    }
}
