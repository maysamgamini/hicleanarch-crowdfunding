using System.Reflection;

namespace ArchitectureTests;

/// <summary>A service's host composes its own module and reaches every other one only through its contracts.</summary>
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
