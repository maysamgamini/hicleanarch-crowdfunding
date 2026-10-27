using System.Text.Json.Nodes;

namespace ContractGate;

/// <summary>
/// Whether one producer's contract (its contract.json snapshot) gives one consumer what it expects. Events:
/// the event is still published, at the version the consumer reads, and every field it requires is always
/// sent with a type it accepts. Endpoints: every field the producer requires is sent by the consumer, and
/// every field the consumer requires in the reply is always answered.
/// </summary>
internal static class Compatibility
{
    public static IReadOnlyList<string> Problems(Expectations consumer, string producer, JsonObject contract)
    {
        var problems = new List<string>();
        var prefix = producer + ".";

        foreach (var (key, expected) in consumer.Events.Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var name = key[prefix.Length..];
            if (contract["events"]?[name] is not JsonObject published)
            {
                problems.Add($"{consumer.Module} handles {key}, which {producer}'s contract {contract["version"]} no longer has");
                continue;
            }

            var version = (int)published["version"]!;
            if (version != expected.Version)
            {
                problems.Add($"{consumer.Module} reads {key} version {expected.Version}; {producer}'s contract {contract["version"]} carries version {version}");
            }

            problems.AddRange(Field.Mismatches("", expected.Requires, Field.FieldsOf(published["schema"]!.AsObject()))
                .Select(m => $"{consumer.Module} reading {key}: {m}"));
        }

        foreach (var (key, expected) in consumer.Endpoints.Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var command = key[prefix.Length..];
            var endpoint = (contract["endpoints"] as JsonObject ?? [])
                .Select(e => e.Value!.AsObject())
                .FirstOrDefault(e => (string)e["command"]! == command);
            if (endpoint is null)
            {
                problems.Add($"{consumer.Module} calls {key}, which {producer}'s contract {contract["version"]} no longer serves");
                continue;
            }

            problems.AddRange(Field.Mismatches("", Field.FieldsOf(endpoint["request"]!.AsObject()), expected.Sends)
                .Select(m => $"{producer} reading {key} from {consumer.Module}: {m}"));
            problems.AddRange(Field.Mismatches("", expected.Requires, Field.FieldsOf(endpoint["response"]!.AsObject()))
                .Select(m => $"{consumer.Module} reading {producer}'s reply to {key}: {m}"));
        }

        return problems;
    }
}
