using System.Text.Json;
using System.Text.Json.Nodes;

using BuildingBlocks.Application;

namespace Payments.Infrastructure;

/// <summary>
/// The card-setup call's JSON while it changes naming (CQA-1519). s4-l05 wrote it in snake_case, s4-l06
/// in the contract's camelCase, on the same path. For one release each side reads either naming, strictly
/// (a missing id fails, it never binds as empty), and writes both, so old and new tasks of either service
/// understand each other while they roll. Payments deploys first; the release after the monolith's
/// drops the snake_case names.
/// </summary>
internal static class CardSetupWire
{
    private static readonly JsonSerializerOptions Snake = new(ContractJson.Options) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The value in both namings: the contract's members, then the same in snake_case.</summary>
    public static JsonObject Both<T>(T value)
    {
        var camel = JsonSerializer.SerializeToNode(value, ContractJson.Options)!.AsObject();
        var snake = JsonSerializer.SerializeToNode(value, Snake)!.AsObject();
        foreach (var (name, node) in snake.ToList())
        {
            if (!camel.ContainsKey(name))
            {
                camel[name] = node?.DeepClone();
            }
        }

        return camel;
    }

    /// <summary>The value in the contract's naming or in snake_case, read strictly; null when it is in neither.</summary>
    public static T? Read<T>(string json)
        where T : class
    {
        foreach (var options in new[] { ContractJson.Options, Snake })
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, options);
            }
            catch (JsonException)
            {
                // try the other naming
            }
        }

        return null;
    }
}
