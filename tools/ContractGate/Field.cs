using System.Text.Json.Nodes;

namespace ContractGate;

/// <summary>
/// One field as a JSON schema describes it: the JSON types it may hold, its format, and, for an object
/// or an array, the fields inside it. Built from the schemas the contract snapshots already hold.
/// </summary>
internal sealed record Field(
    IReadOnlyList<string> Types,
    string? Format = null,
    IReadOnlyDictionary<string, Field>? Fields = null,
    Field? Items = null)
{
    /// <summary>
    /// The field a schema describes. With <paramref name="everyProperty"/> false an object keeps only its
    /// required properties (what a reader needs, or what a writer always sends); with true, all of them
    /// (what a writer may send).
    /// </summary>
    public static Field Of(JsonObject schema, bool everyProperty = false)
    {
        var types = schema["type"] switch
        {
            JsonArray many => [.. many.Select(t => (string)t!).Order(StringComparer.Ordinal)],
            JsonValue one => [(string)one!],
            _ => schema["properties"] is not null ? ["object"] : (IReadOnlyList<string>)[],
        };

        return new Field(
            types,
            (string?)schema["format"],
            schema["properties"] is JsonObject properties ? PropertiesOf(properties, schema["required"], everyProperty) : null,
            schema["items"] is JsonObject items ? Of(items, everyProperty) : null);
    }

    /// <summary>The top-level fields of a message's schema.</summary>
    public static IReadOnlyDictionary<string, Field> FieldsOf(JsonObject schema, bool everyProperty = false) =>
        schema["properties"] is JsonObject properties ? PropertiesOf(properties, schema["required"], everyProperty) : new Dictionary<string, Field>();

    /// <summary>
    /// What is wrong when <paramref name="writer"/> sends this field to <paramref name="reader"/>: a type
    /// or format the reader does not take, or a field the reader requires that the writer does not always send.
    /// </summary>
    public static IEnumerable<string> Mismatches(string path, Field reader, Field writer)
    {
        var extra = writer.Types.Except(reader.Types, StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
        {
            yield return $"{path} may be {string.Join(" or ", extra)}, which the reader does not accept";
        }

        if (reader.Format is not null && reader.Format != writer.Format)
        {
            yield return $"{path} is read as {reader.Format}, written as {writer.Format ?? "no format"}";
        }

        foreach (var mismatch in Mismatches(path, reader.Fields, writer.Fields))
        {
            yield return mismatch;
        }

        if (reader.Items is not null)
        {
            if (writer.Items is null)
            {
                yield return $"{path}[] is read as a list, and the writer sends none";
            }
            else
            {
                foreach (var mismatch in Mismatches(path + "[]", reader.Items, writer.Items))
                {
                    yield return mismatch;
                }
            }
        }
    }

    /// <summary>Every field the reader requires, checked against what the writer sends.</summary>
    public static IEnumerable<string> Mismatches(string path, IReadOnlyDictionary<string, Field>? reader, IReadOnlyDictionary<string, Field>? writer)
    {
        foreach (var (name, field) in reader ?? new Dictionary<string, Field>())
        {
            var where = path.Length == 0 ? name : $"{path}.{name}";
            if (writer is null || !writer.TryGetValue(name, out var sent))
            {
                yield return $"{where} is required by the reader and not always sent";
                continue;
            }

            foreach (var mismatch in Mismatches(where, field, sent))
            {
                yield return mismatch;
            }
        }
    }

    private static Dictionary<string, Field> PropertiesOf(JsonObject properties, JsonNode? required, bool everyProperty)
    {
        var names = required is JsonArray list ? list.Select(n => (string)n!).ToHashSet(StringComparer.Ordinal) : [];
        return properties
            .Where(p => everyProperty || names.Contains(p.Key))
            .ToDictionary(p => p.Key, p => Of(p.Value!.AsObject(), everyProperty), StringComparer.Ordinal);
    }
}
