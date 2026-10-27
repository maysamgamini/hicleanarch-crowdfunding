using System.Reflection;

using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

namespace ArchitectureTests;

/// <summary>
/// A module's outbox and transaction are keyed services named after the module, and the names live in
/// Contracts, where every module can read them. A module may still only take its own: an event written
/// to another module's outbox, or a change made in its transaction, would cross the boundary (CQA-507).
/// </summary>
public sealed class KeyedServiceRulesTests
{
    private static readonly string[] Modules = ["Campaigns", "Pledges", "Payments", "Notifications"];

    private static readonly Type[] PerModule = [typeof(IOutbox), typeof(ITransactionScope)];

    private static IEnumerable<Assembly> ModuleAssemblies() =>
        Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => Modules.Contains(n!.Split('.')[0]))
            .Select(n => Assembly.Load(n!));

    [Fact]
    public void A_module_takes_only_its_own_outbox_and_transaction()
    {
        var asked = ModuleAssemblies()
            .SelectMany(a => a.GetTypes().Select(t => (Module: a.GetName().Name!.Split('.')[0], Type: t)))
            .SelectMany(x => x.Type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Where(p => PerModule.Contains(p.ParameterType))
                .Select(p => (x.Module, x.Type, Key: p.GetCustomAttribute<FromKeyedServicesAttribute>()?.Key as string)))
            .ToList();

        Assert.NotEmpty(asked);
        Assert.Empty(asked.Where(a => a.Key != a.Module).Select(a => $"{a.Type.FullName} asks for the {a.Key ?? "unkeyed"} one, in {a.Module}"));
    }
}
