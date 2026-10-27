using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractGate;

/// <summary>An event a module handles from another module: the version it reads and the fields it requires.</summary>
internal sealed record EventExpectation(int Version, IReadOnlyDictionary<string, Field> Requires);

/// <summary>A remote endpoint a module calls: what it sends, and what it requires in the reply.</summary>
internal sealed record EndpointExpectation(IReadOnlyDictionary<string, Field> Sends, IReadOnlyDictionary<string, Field> Requires);

/// <summary>
/// What one module needs from the others, as it was built, kept beside the module in
/// src/Services/&lt;Module&gt;/expectations.json (src/Modules/Pledges until s5-l12). Keys are "&lt;Producer&gt;.&lt;Name&gt;". <see cref="InProcess"/>
/// lists the other modules' commands, queries and types it uses in the same process: possible only while
/// both modules run in one service.
/// </summary>
internal sealed record Expectations(
    string Module,
    IReadOnlyDictionary<string, EventExpectation> Events,
    IReadOnlyDictionary<string, EndpointExpectation> Endpoints,
    IReadOnlyList<string> InProcess)
{
    /// <summary>Where a module's expectations are at this commit: in its service's folder (src/Services/&lt;Module&gt;/),
    /// Pledges' under src/Modules until s5-l12 moves it.</summary>
    public static string PathOf(string module) =>
        module == "Pledges" ? $"src/Modules/{module}/expectations.json" : $"src/Services/{module}/expectations.json";

    /// <summary>Every place a module's expectations have been, newest first: a recorded version may predate a move.</summary>
    public static IReadOnlyList<string> PathsOf(string module) =>
        [$"src/Services/{module}/expectations.json", $"src/Modules/{module}/expectations.json"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Whether this module uses <paramref name="type"/> ("&lt;Producer&gt;.&lt;Name&gt;") in any way.</summary>
    public bool Uses(string type) => Events.ContainsKey(type) || Endpoints.ContainsKey(type) || InProcess.Contains(type, StringComparer.Ordinal);

    public static Expectations Read(string json) => JsonSerializer.Deserialize<Expectations>(json, Json)
        ?? throw new InvalidDataException("An expectations file is empty.");

    /// <summary>Written with every key in order, so the file changes only when an expectation does.</summary>
    public string Write() => Sorted(JsonSerializer.SerializeToNode(this, Json)!).ToJsonString(Json) + "\n";

    private static JsonNode Sorted(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Sorted(p.Value)))
            .ToList()),
        JsonArray array => new JsonArray([.. array.Select(i => i is null ? null : Sorted(i))]),
        _ => node.DeepClone(),
    };
}
