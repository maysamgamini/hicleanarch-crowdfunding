using System.Reflection;

namespace ArchitectureTests;

/// <summary>
/// The dependency rules, checked on the compiled assemblies. A project reference that breaks a rule
/// compiles; these tests are what refuse it.
/// </summary>
public class LayerRulesTests
{
    private static readonly string[] Modules = ["Campaigns", "Pledges", "Payments", "Notifications"];

    private static readonly string[] SharedBlocks = ["BuildingBlocks.Domain", "BuildingBlocks.Application"];

    /// <summary>Every assembly of ours that ships with the host.</summary>
    private static readonly Assembly[] Ours = LoadOurs();

    public static TheoryData<string> OurAssemblies() => [.. Ours.Select(a => a.GetName().Name!)];

    [Fact]
    public void The_host_and_every_layer_of_every_module_are_checked() =>
        Assert.Superset(
            new HashSet<string> { "CrowdFunding.Host", "Campaigns.Domain", "Campaigns.Application", "Campaigns.Infrastructure", "Campaigns.Contracts",
                "Pledges.Domain", "Pledges.Application", "Pledges.Infrastructure", "Pledges.Contracts",
                "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.Contracts",
                "BuildingBlocks.Domain", "BuildingBlocks.Application", "BuildingBlocks.Infrastructure" },
            Ours.Select(a => a.GetName().Name!).ToHashSet());

    [Theory]
    [MemberData(nameof(OurAssemblies))]
    public void A_domain_depends_only_on_the_shared_domain(string name)
    {
        if (!name.EndsWith(".Domain", StringComparison.Ordinal))
        {
            return;
        }

        Assert.All(ReferencesOf(name), r => Assert.Equal("BuildingBlocks.Domain", r));
    }

    [Theory]
    [MemberData(nameof(OurAssemblies))]
    public void An_application_layer_depends_only_on_its_domain_and_the_shared_blocks(string name)
    {
        if (!name.EndsWith(".Application", StringComparison.Ordinal))
        {
            return;
        }

        var allowed = new[] { "BuildingBlocks.Domain", "BuildingBlocks.Application", ModuleOf(name) + ".Domain" };
        Assert.All(ReferencesOf(name), r => Assert.True(allowed.Contains(r) || r.EndsWith(".Contracts", StringComparison.Ordinal),
            $"{name} references {r}"));
    }

    [Theory]
    [MemberData(nameof(OurAssemblies))]
    public void A_contract_depends_only_on_the_shared_blocks(string name)
    {
        if (!name.EndsWith(".Contracts", StringComparison.Ordinal))
        {
            return;
        }

        Assert.All(ReferencesOf(name), r => Assert.Contains(r, SharedBlocks));
    }

    [Theory]
    [MemberData(nameof(OurAssemblies))]
    public void A_module_sees_another_module_only_through_its_contracts(string name)
    {
        var module = ModuleOf(name);
        if (module is "BuildingBlocks" or "CrowdFunding")
        {
            return;
        }

        var foreign = ReferencesOf(name).Where(r => ModuleOf(r) != module && Modules.Contains(ModuleOf(r)));
        Assert.All(foreign, r => Assert.EndsWith(".Contracts", r, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(OurAssemblies))]
    public void Building_blocks_know_no_module(string name)
    {
        if (ModuleOf(name) != "BuildingBlocks")
        {
            return;
        }

        Assert.DoesNotContain(ReferencesOf(name), r => Modules.Contains(ModuleOf(r)));
    }

    private static string ModuleOf(string assemblyName) => assemblyName.Split('.')[0];

    private static IEnumerable<string> ReferencesOf(string name) =>
        Ours.Single(a => a.GetName().Name == name).GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(IsOurs);

    private static bool IsOurs(string name) =>
        name == "CrowdFunding.Host" || Modules.Append("BuildingBlocks").Contains(ModuleOf(name));

    private static Assembly[] LoadOurs() =>
        [.. Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => IsOurs(n!))
            .Select(n => Assembly.Load(n!))];
}
