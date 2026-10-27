using System.Text.Json;
using System.Text.Json.Nodes;

using BuildingBlocks.Application;

namespace Contracts.ProducerTests;

/// <summary>
/// Each package ships recorded examples (contract/examples) of every version still allowed: an event as
/// <c>&lt;Event&gt;.v&lt;version&gt;.json</c>, an endpoint as <c>&lt;Command&gt;.request.json</c> and
/// <c>.response.json</c>. Consumers' tests read them; here the producer checks they are still true.
/// </summary>
public class ExampleTests
{
    public static TheoryData<string> Modules() => [.. Packages.ByModule.Keys];

    [Theory]
    [MemberData(nameof(Modules))]
    public void Every_event_and_endpoint_has_a_recorded_example(string module)
    {
        var current = Packages.Current(module);
        var expected = current["events"]!.AsObject().Select(e => $"{e.Key}.v{(int)e.Value!["version"]!}.json")
            .Concat(current["endpoints"]!.AsObject().SelectMany(e => new[] { $"{(string)e.Value!["command"]!}.request.json", $"{(string)e.Value!["command"]!}.response.json" }));

        Assert.All(expected, file => Assert.True(File.Exists(Path.Combine(Examples(module), file)), $"{module}.Contracts has no example {file}."));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Every_example_reads_strictly_and_names_only_fields_the_contract_still_has(string module)
    {
        var current = Packages.Current(module);
        var types = Packages.ByModule[module].GetExportedTypes().ToDictionary(t => t.Name);
        Assert.All(Directory.GetFiles(Examples(module), "*.json"), path =>
        {
            var file = Path.GetFileName(path);
            var parts = file.Split('.');
            var (type, schema) = parts[1] switch
            {
                "request" => Endpoint(current, parts[0], "request", types),
                "response" => Endpoint(current, parts[0], "response", types),
                _ => Event(current, parts[0], int.Parse(parts[1].TrimStart('v'), System.Globalization.CultureInfo.InvariantCulture), types, file),
            };
            var json = File.ReadAllText(path);

            Assert.NotNull(JsonSerializer.Deserialize(json, type, ContractJson.Options));
            Assert.Empty(UnknownFields(JsonNode.Parse(json)!, schema, file));
        });
    }

    private static string Examples(string module) => Path.Combine(Packages.ContractDirectory(module), "examples");

    private static (Type, JsonObject) Event(JsonObject current, string name, int version, Dictionary<string, Type> types, string file)
    {
        var entry = current["events"]![name];
        Assert.True(entry is not null && types.ContainsKey(name), $"{file} is an example of an event the contract no longer has.");
        Assert.True(version <= (int)entry["version"]!, $"{file} is newer than the event's version.");
        return (types[name], entry["schema"]!.AsObject());
    }

    private static (Type, JsonObject) Endpoint(JsonObject current, string command, string side, Dictionary<string, Type> types)
    {
        var endpoint = current["endpoints"]!.AsObject().Select(e => e.Value!).SingleOrDefault(e => (string)e["command"]! == command);
        Assert.True(endpoint is not null, $"{command} is not a remote endpoint of the contract.");
        var type = side == "request" ? types[command] : ContractSnapshot.ReplyOf(types[command]);
        return (type, endpoint[side]!.AsObject());
    }

    /// <summary>The example's fields that the schema does not have: a field the producer once sent and has since removed.</summary>
    private static IEnumerable<string> UnknownFields(JsonNode example, JsonObject schema, string path)
    {
        if (example is JsonObject fields && schema["properties"] is JsonObject properties)
        {
            foreach (var (name, value) in fields)
            {
                if (properties[name] is not JsonObject field)
                {
                    yield return $"{path}: {name}";
                }
                else if (value is not null)
                {
                    foreach (var unknown in UnknownFields(value, field, $"{path}: {name}"))
                    {
                        yield return unknown;
                    }
                }
            }
        }
        else if (example is JsonArray items && schema["items"] is JsonObject item)
        {
            foreach (var unknown in items.Where(i => i is not null).SelectMany(i => UnknownFields(i!, item, path + "[]")))
            {
                yield return unknown;
            }
        }
    }
}
