using System.Reflection;

namespace ArchitectureTests;

/// <summary>
/// A service's host composes its own module and reaches every other one only through its contracts (and, for
/// Pledges, Payments' client). Since s5-l10 there are four services and no monolith.
/// </summary>
public class ServiceHostRulesTests
{
    private static readonly string[] OtherModules = ["Campaigns", "Pledges", "Notifications"];

    [Fact]
    public void The_payments_host_references_no_other_module_except_its_contracts()
    {
        var host = Assembly.Load("CrowdFunding.Payments.Host");
        var reachable = Closure(host).Where(n => OtherModules.Contains(n.Split('.')[0])).ToList();

        Assert.NotEmpty(reachable); // Payments reads the other modules' contracts, so the check sees something
        Assert.All(reachable, name => Assert.EndsWith(".Contracts", name, StringComparison.Ordinal));
    }

    [Fact]
    public void The_payments_host_composes_the_payments_module()
    {
        Assert.Contains("Payments.Infrastructure", Closure(Assembly.Load("CrowdFunding.Payments.Host")));
    }

    /// <summary>Lesson s5-l10: four services, each host composing one module; no monolith is left.</summary>
    public static TheoryData<string, string> Hosts() => new()
    {
        { "CrowdFunding.Pledges.Host", "Pledges" },
        { "CrowdFunding.Payments.Host", "Payments" },
        { "CrowdFunding.Notifications.Host", "Notifications" },
        { "CrowdFunding.Campaigns.Host", "Campaigns" },
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void No_service_host_composes_two_modules(string host, string module)
    {
        string[] modules = ["Campaigns", "Pledges", "Payments", "Notifications"];
        // A module's own code, not its contracts nor the client a caller is built on (Payments.Client, Pledges' own).
        var composed = Closure(Assembly.Load(host))
            .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal) && n != "Payments.Client")
            .Select(n => n.Split('.')[0])
            .Where(modules.Contains)
            .ToHashSet();

        Assert.Equal([module], composed);
    }

    [Fact]
    public void No_monolith_host_is_left()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CrowdFunding.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.False(Directory.Exists(Path.Combine(root, "src", "Host")));
        Assert.False(File.Exists(Path.Combine(root, "Dockerfile")));
        Assert.DoesNotContain("src/Host/", File.ReadAllText(Path.Combine(root, "CrowdFunding.slnx")), StringComparison.Ordinal);
    }

    /// <summary>Every assembly of ours the host loads, directly or through another of ours.</summary>
    private static HashSet<string> Closure(Assembly root)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<Assembly>([root]);
        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                if (reference.Name is { } name && IsOurs(name) && seen.Add(name))
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return seen;
    }

    private static bool IsOurs(string name) =>
        name.StartsWith("Campaigns.", StringComparison.Ordinal) || name.StartsWith("Pledges.", StringComparison.Ordinal)
        || name.StartsWith("Payments.", StringComparison.Ordinal) || name.StartsWith("Notifications", StringComparison.Ordinal)
        || name.StartsWith("CrowdFunding.BuildingBlocks.", StringComparison.Ordinal);
}
