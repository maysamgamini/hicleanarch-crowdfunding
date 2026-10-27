using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

using BuildingBlocks.Application;

namespace Contracts.ProducerTests;

/// <summary>One difference between two snapshots of a contract, in the message it belongs to.</summary>
internal sealed record ContractChange(string Message, string Detail, bool Breaking)
{
    public override string ToString() => $"{Message}: {Detail}{(Breaking ? " (breaking)" : "")}";
}

/// <summary>
/// A contract package as it travels: the JSON schema of every event, with the event's version, and of
/// every remote endpoint's request and reply, written with the strict options every reader uses. The
/// rules of contract-evolution are applied to two snapshots: within a major version a contract only
/// gains optional fields and new messages.
/// </summary>
internal static class ContractSnapshot
{
    private static readonly JsonSchemaExporterOptions Exporter = new() { TreatNullObliviousAsNonNullable = true };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonObject Of(Assembly contracts)
    {
        var version = contracts.GetName().Version!;
        return Of("CrowdFunding." + contracts.GetName().Name, new Version(version.Major, version.Minor, version.Build), contracts.GetExportedTypes());
    }

    public static JsonObject Of(string package, Version version, IEnumerable<Type> types)
    {
        var exported = types.ToList();
        var events = new JsonObject();
        foreach (var type in exported.Where(t => t.IsSubclassOf(typeof(IntegrationEvent)) && !t.IsAbstract).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            events[type.Name] = new JsonObject { ["version"] = VersionOf(type), ["schema"] = SchemaOf(type) };
        }

        var endpoints = new JsonObject();
        foreach (var (type, endpoint) in exported
                     .Select(t => (Type: t, Endpoint: t.GetCustomAttribute<RemoteEndpointAttribute>()))
                     .Where(e => e.Endpoint is not null)
                     .OrderBy(e => e.Endpoint!.Path, StringComparer.Ordinal))
        {
            endpoints[$"{endpoint!.Method} {endpoint.Path}"] = new JsonObject
            {
                ["command"] = type.Name,
                ["request"] = SchemaOf(type),
                ["response"] = SchemaOf(ReplyOf(type)),
            };
        }

        return new JsonObject
        {
            ["package"] = package,
            ["version"] = version.ToString(3),
            ["events"] = events,
            ["endpoints"] = endpoints,
        };
    }

    public static string Write(JsonObject snapshot) => snapshot.ToJsonString(Indented) + "\n";

    /// <summary>The version an event carries in its envelope, read without making one.</summary>
    public static int VersionOf(Type eventType) => ((IntegrationEvent)RuntimeHelpers.GetUninitializedObject(eventType)).Version;

    public static Type ReplyOf(Type command) =>
        command.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)).GetGenericArguments()[0];

    public static JsonObject SchemaOf(Type type) => (JsonObject)Sorted(JsonSchemaExporter.GetJsonSchemaAsNode(ContractJson.Options, type, Exporter));

    /// <summary>Every difference from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static IReadOnlyList<ContractChange> Compare(JsonObject before, JsonObject after)
    {
        var changes = new List<ContractChange>();
        Messages("event", before["events"]!.AsObject(), after["events"]!.AsObject(), changes, (name, b, a) =>
        {
            var (from, to) = ((int)b["version"]!, (int)a["version"]!);
            if (from != to)
            {
                changes.Add(new(name, $"version {from} -> {to}", Breaking: true));
            }

            Schema(name, "", b["schema"]!.AsObject(), a["schema"]!.AsObject(), changes);
        });
        Messages("endpoint", before["endpoints"]!.AsObject(), after["endpoints"]!.AsObject(), changes, (name, b, a) =>
        {
            if ((string)b["command"]! != (string)a["command"]!)
            {
                changes.Add(new(name, $"carries {(string)a["command"]!} instead of {(string)b["command"]!}", Breaking: true));
            }

            Schema(name, "request", b["request"]!.AsObject(), a["request"]!.AsObject(), changes);
            Schema(name, "response", b["response"]!.AsObject(), a["response"]!.AsObject(), changes);
        });
        return changes;
    }

    /// <summary>
    /// What is wrong with going from <paramref name="before"/> to <paramref name="after"/>; empty when the
    /// versions say what changed. A breaking change takes a new major version, and an event changed in a
    /// breaking way also a higher event version; an addition takes at least a new minor version.
    /// </summary>
    public static IReadOnlyList<string> Problems(JsonObject before, JsonObject after)
    {
        var changes = Compare(before, after);
        var (from, to) = (Version.Parse((string)before["version"]!), Version.Parse((string)after["version"]!));
        var problems = new List<string>();
        if (to < from)
        {
            problems.Add($"The version went down, from {from} to {to}.");
        }

        var breaking = changes.Where(c => c.Breaking).ToList();
        if (breaking.Count > 0 && to.Major <= from.Major)
        {
            problems.Add($"Breaking changes need a new major version (at least {from.Major + 1}.0.0), not {to}: "
                + string.Join("; ", breaking) + ". Within a major version, add an optional field or a new event type beside the old one.");
        }

        var events = after["events"]!.AsObject();
        foreach (var name in breaking.Select(c => c.Message).Distinct().Where(n => before["events"]!.AsObject().ContainsKey(n) && events.ContainsKey(n)))
        {
            if ((int)events[name]!["version"]! <= (int)before["events"]![name]!["version"]!)
            {
                problems.Add($"{name} changed in a breaking way but still carries version {(int)events[name]!["version"]!}: raise its Version, so readers can tell the shapes apart.");
            }
        }

        if (breaking.Count == 0 && changes.Count > 0 && to.Major == from.Major && to.Minor <= from.Minor)
        {
            problems.Add($"Additions need at least a new minor version ({from.Major}.{from.Minor + 1}.0), not {to}: " + string.Join("; ", changes) + ".");
        }

        return problems;
    }

    private static void Messages(string kind, JsonObject before, JsonObject after, List<ContractChange> changes, Action<string, JsonObject, JsonObject> both)
    {
        foreach (var (name, node) in before)
        {
            if (after[name] is JsonObject other)
            {
                both(name, node!.AsObject(), other);
            }
            else
            {
                changes.Add(new(name, $"{kind} removed", Breaking: true));
            }
        }

        foreach (var (name, _) in after.Where(m => !before.ContainsKey(m.Key)))
        {
            changes.Add(new(name, $"{kind} added", Breaking: false));
        }
    }

    private static readonly HashSet<string> Structure = ["properties", "required", "items"];

    private static void Schema(string message, string path, JsonObject before, JsonObject after, List<ContractChange> changes)
    {
        var where = path.Length == 0 ? "the message" : path;
        foreach (var keyword in before.Select(k => k.Key).Union(after.Select(k => k.Key)).Where(k => !Structure.Contains(k)))
        {
            if (!JsonNode.DeepEquals(before[keyword], after[keyword]))
            {
                changes.Add(new(message, $"{where}: {keyword} {Show(before[keyword])} -> {Show(after[keyword])}", Breaking: true));
            }
        }

        if (before["items"] is JsonObject itemsBefore && after["items"] is JsonObject itemsAfter)
        {
            Schema(message, path + "[]", itemsBefore, itemsAfter, changes);
        }
        else if (before["items"] is not null || after["items"] is not null)
        {
            changes.Add(new(message, $"{where}: items changed", Breaking: true));
        }

        var propertiesBefore = before["properties"] as JsonObject ?? [];
        var propertiesAfter = after["properties"] as JsonObject ?? [];
        var requiredBefore = Names(before["required"]);
        var requiredAfter = Names(after["required"]);
        foreach (var (name, schema) in propertiesBefore)
        {
            var field = path.Length == 0 ? name : $"{path}.{name}";
            if (propertiesAfter[name] is not JsonObject schemaAfter)
            {
                changes.Add(new(message, $"{field} removed", Breaking: true));
                continue;
            }

            if (requiredBefore.Contains(name) != requiredAfter.Contains(name))
            {
                changes.Add(new(message, requiredAfter.Contains(name) ? $"{field} now required" : $"{field} no longer required", Breaking: true));
            }

            Schema(message, field, schema!.AsObject(), schemaAfter, changes);
        }

        foreach (var (name, _) in propertiesAfter.Where(p => !propertiesBefore.ContainsKey(p.Key)))
        {
            var field = path.Length == 0 ? name : $"{path}.{name}";
            changes.Add(requiredAfter.Contains(name)
                ? new(message, $"{field} added as a required field", Breaking: true)
                : new(message, $"{field} added as an optional field", Breaking: false));
        }
    }

    private static HashSet<string> Names(JsonNode? required) =>
        required is JsonArray names ? [.. names.Select(n => (string)n!)] : [];

    private static string Show(JsonNode? node) => node?.ToJsonString() ?? "(none)";

    /// <summary>Keywords, properties and required names in a fixed order, so a snapshot's diff shows only real changes.</summary>
    private static JsonNode Sorted(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => Structure.Contains(p.Key) ? 1 : 0).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, p.Key == "required" && p.Value is JsonArray names
                ? new JsonArray([.. names.Select(n => (string)n!).Order(StringComparer.Ordinal).Select(n => (JsonNode)JsonValue.Create(n))])
                : p.Value is null ? null : Sorted(p.Value)))
            .ToList()),
        JsonArray array => new JsonArray([.. array.Select(i => i is null ? null : Sorted(i))]),
        _ => node.DeepClone(),
    };
}
