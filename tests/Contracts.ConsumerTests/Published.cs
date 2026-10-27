using System.Reflection;
using System.Text.Json.Nodes;

using BuildingBlocks.Application;

namespace Contracts.ConsumerTests;

/// <summary>
/// What the consumers read: every event a module handles from another module's Contracts, and the
/// recorded examples and schema that ship in that Contracts package.
/// </summary>
internal static class Published
{
    private static readonly string[] Modules = ["Campaigns", "Pledges", "Payments", "Notifications"];

    /// <summary>Each module's handler assemblies, as the host loads them.</summary>
    private static readonly Assembly[] Handlers = [.. Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(n => Modules.Any(m => n == m || n!.StartsWith(m + ".", StringComparison.Ordinal)) && !n!.EndsWith(".Contracts", StringComparison.Ordinal))
        .Select(n => Assembly.Load(n!))];

    /// <summary>(consumer module, event type) for every handler of an event another module publishes.</summary>
    public static readonly IReadOnlyList<(string Consumer, Type Event)> Consumed = [.. Handlers
        .SelectMany(a => a.GetTypes())
        .SelectMany(t => t.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>))
            .Select(i => (Consumer: ModuleOf(t.Assembly), Event: i.GetGenericArguments()[0])))
        .Where(c => c.Consumer != ProducerOf(c.Event))
        .Distinct()
        .OrderBy(c => c.Consumer, StringComparer.Ordinal).ThenBy(c => c.Event.Name, StringComparer.Ordinal)];

    public static string ProducerOf(Type message) => message.Assembly.GetName().Name![..^".Contracts".Length];

    public static Type Type(string producer, string name) =>
        Consumed.Select(c => c.Event).Concat(typeof(Payments.Contracts.StartCardSetup).Assembly.GetExportedTypes())
            .First(t => ProducerOf(t) == producer && t.Name == name);

    public static string Examples(string producer) => Path.Combine(AppContext.BaseDirectory, "contract", producer, "examples");

    public static IEnumerable<string> ExamplesOf(string producer, string eventType) =>
        Directory.Exists(Examples(producer))
            ? Directory.GetFiles(Examples(producer), $"{eventType}.v*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal)!
            : [];

    public static JsonObject Example(string producer, string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Examples(producer), file)))!.AsObject();

    public static JsonObject Contract(string producer) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", producer, "contract.json")))!.AsObject();

    private static string ModuleOf(Assembly assembly) => assembly.GetName().Name!.Split('.')[0];
}
