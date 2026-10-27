using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BuildingBlocks.Application;

/// <summary>
/// How every message that crosses a module's boundary as JSON is written and read: the web defaults
/// (camelCase, unknown members ignored), plus two rules that keep a tolerant reader from hiding a
/// broken message. A constructor parameter without a default must be present, and a member that is
/// not nullable must not be null; either fails with a <see cref="JsonException"/> instead of becoming
/// null or 0. An optional member is a nullable parameter with a default, and its reader says what a
/// missing value means.
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        return options;
    }
}
