using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

using BuildingBlocks.Application;

using ContractGate;

namespace Contracts.ConsumerTests;

/// <summary>
/// What a module needs from the others, read from the module as it is built against the published
/// Contracts packages:
/// <list type="bullet">
/// <item>every event it handles from another module, with the event version and the fields its type requires;</item>
/// <item>every remote endpoint it calls, with what its request writes and what it requires in the reply;</item>
/// <item>every other type of another module's Contracts it refers to: a command, a query or a reply used in process.</item>
/// </list>
/// The references come from the module's assembly metadata, so nothing the code uses can be left out.
/// </summary>
internal static class ModuleExpectations
{
    public static readonly string[] Modules = ["Campaigns", "Notifications", "Payments", "Pledges"];

    private static readonly JsonSchemaExporterOptions Exporter = new() { TreatNullObliviousAsNonNullable = true };

    public static Expectations Of(string module)
    {
        var used = ContractTypesUsedBy(module).Distinct().ToList();

        var events = Published.Consumed.Where(c => c.Consumer == module)
            .ToDictionary(
                c => Key(c.Event),
                c => new EventExpectation(((IntegrationEvent)RuntimeHelpers.GetUninitializedObject(c.Event)).Version, Field.FieldsOf(Schema(c.Event))),
                StringComparer.Ordinal);

        var commands = used.Where(t => t.GetCustomAttribute<RemoteEndpointAttribute>() is not null).ToList();
        var endpoints = commands.ToDictionary(
            Key,
            t => new EndpointExpectation(Field.FieldsOf(Schema(t), everyProperty: true), Field.FieldsOf(Schema(ReplyOf(t)))),
            StringComparer.Ordinal);

        // A message's own parts (a tier inside CampaignPublished, a reply) travel with it.
        var messages = Published.Consumed.Where(c => c.Consumer == module).Select(c => c.Event).Concat(commands).Concat(commands.Select(ReplyOf));
        var accounted = messages.SelectMany(PartsOf).ToHashSet();
        var inProcess = used
            .Where(t => !t.IsSubclassOf(typeof(IntegrationEvent)) && !accounted.Contains(t))
            .Select(Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        return new Expectations(module, events, endpoints, inProcess);
    }

    private static string Key(Type type) => $"{Published.ProducerOf(type)}.{type.Name}";

    /// <summary>The type and every type its properties hold, lists included.</summary>
    private static IEnumerable<Type> PartsOf(Type type)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>([type]);
        while (pending.TryPop(out var next))
        {
            if (!seen.Add(next))
            {
                continue;
            }

            foreach (var property in next.GetProperties())
            {
                var held = property.PropertyType;
                pending.Push(held.IsGenericType ? held.GetGenericArguments()[0] : held.GetElementType() ?? held);
            }
        }

        return seen;
    }

    private static JsonObject Schema(Type type) => JsonSchemaExporter.GetJsonSchemaAsNode(ContractJson.Options, type, Exporter).AsObject();

    private static Type ReplyOf(Type command) =>
        command.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)).GetGenericArguments()[0];

    /// <summary>Types of other modules' Contracts that the module's own assemblies refer to.</summary>
    private static IEnumerable<Type> ContractTypesUsedBy(string module)
    {
        var files = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Where(f => Path.GetFileNameWithoutExtension(f) is var n && (n == module || n.StartsWith(module + ".", StringComparison.Ordinal)) && !n.EndsWith(".Contracts", StringComparison.Ordinal));

        foreach (var file in files)
        {
            using var pe = new PEReader(File.OpenRead(file));
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeReferences)
            {
                var reference = metadata.GetTypeReference(handle);
                if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
                {
                    continue;
                }

                var assembly = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                if (!assembly.EndsWith(".Contracts", StringComparison.Ordinal) || assembly == module + ".Contracts")
                {
                    continue;
                }

                var name = $"{metadata.GetString(reference.Namespace)}.{metadata.GetString(reference.Name)}";
                if (Assembly.Load(assembly).GetType(name) is { IsPublic: true } type)
                {
                    yield return type;
                }
            }
        }
    }
}
