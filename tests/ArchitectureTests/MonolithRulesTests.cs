using System.Reflection;
using System.Xml.Linq;

namespace ArchitectureTests;

/// <summary>
/// The rules of the old monolith's host, which since s5-l10 is the Pledges service (Pledges.Host).
/// Payments has left the monolith (lesson s4-l10). The monolith knows Payments only through the
/// Payments.Contracts package, which its client is built on (StartCardSetup over HTTP, s4-l03): no
/// Payments domain, application or infrastructure, and so no Payments endpoint, job, handler, table or
/// migration in its image. Checked twice: on the compiled assemblies, and on the project files, where a
/// reference would be added. Notifications left too (lesson s5-l04): nothing of it, not even its
/// contracts' consumer code, so the monolith cannot send email or read users even by mistake. And
/// Campaigns (lesson s5-l09): nothing of it but its contracts, whose events Pledges reads.
/// </summary>
public class MonolithRulesTests
{
    /// <summary>What the monolith may load of Payments: its contracts, and the client built on them.</summary>
    private static readonly string[] PaymentsAllowed = ["Payments.Contracts", "Payments.Client"];

    [Fact]
    public void The_monolith_loads_nothing_of_payments_but_its_contracts_and_its_client()
    {
        var reachable = Closure(Assembly.Load("CrowdFunding.Pledges.Host"));

        Assert.Contains("Payments.Contracts", reachable); // the check sees Payments at all
        Assert.All(reachable.Where(n => n.StartsWith("Payments.", StringComparison.Ordinal)),
            name => Assert.Contains(name, PaymentsAllowed));
    }

    [Fact]
    public void The_monolith_s_project_graph_reaches_no_payments_project_but_its_client()
    {
        var graph = ProjectClosure("src/Services/Pledges/Pledges.Host/Pledges.Host.csproj");

        Assert.All(graph.Where(p => p.StartsWith("src/Modules/Payments", StringComparison.Ordinal)),
            project => Assert.Equal("src/Modules/Payments.Client/Payments.Client.csproj", project));
        Assert.DoesNotContain(graph, p => p.StartsWith("src/Services/", StringComparison.Ordinal) && !p.StartsWith("src/Services/Pledges/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_monolith_gets_payments_contracts_as_the_published_package()
    {
        var packages = ProjectClosure("src/Services/Pledges/Pledges.Host/Pledges.Host.csproj")
            .SelectMany(p => XDocument.Load(Path.Combine(Root(), p)).Descendants("PackageReference"))
            .Select(r => (string)r.Attribute("Include")!);

        Assert.Contains("CrowdFunding.Payments.Contracts", packages);
    }

    [Fact]
    public void The_monolith_references_and_loads_nothing_of_notifications()
    {
        var graph = ProjectClosure("src/Services/Pledges/Pledges.Host/Pledges.Host.csproj");
        var loaded = Closure(Assembly.Load("CrowdFunding.Pledges.Host"));

        Assert.Contains("Pledges.Infrastructure", loaded); // the check sees the modules it does compose
        Assert.DoesNotContain(graph, p => p.StartsWith("src/Services/Notifications/", StringComparison.Ordinal));
        Assert.DoesNotContain(loaded, name => name.StartsWith("Notifications", StringComparison.Ordinal));
    }

    [Fact]
    public void The_monolith_references_and_loads_nothing_of_campaigns_but_its_contracts()
    {
        // Lesson s5-l09: Campaigns runs only in its own service; Pledges reads its events, through its contracts.
        var graph = ProjectClosure("src/Services/Pledges/Pledges.Host/Pledges.Host.csproj");
        var loaded = Closure(Assembly.Load("CrowdFunding.Pledges.Host"));

        Assert.Contains("Campaigns.Contracts", loaded); // the check sees Campaigns at all
        Assert.DoesNotContain(graph, p => p.StartsWith("src/Modules/Campaigns", StringComparison.Ordinal) && !p.EndsWith(".Contracts.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(graph, p => p.StartsWith("src/Services/Campaigns/", StringComparison.Ordinal));
        Assert.All(loaded.Where(n => n.StartsWith("Campaigns.", StringComparison.Ordinal)), name => Assert.Equal("Campaigns.Contracts", name));
    }

    /// <summary>Every assembly of ours the program loads, directly or through another of ours.</summary>
    private static HashSet<string> Closure(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
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
        || name.StartsWith("CrowdFunding.", StringComparison.Ordinal);

    /// <summary>A project and every project it references, as repository paths.</summary>
    private static HashSet<string> ProjectClosure(string project)
    {
        var root = Root();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([project]);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(Path.Combine(root, current))!;
            foreach (var reference in XDocument.Load(Path.Combine(root, current)).Descendants("ProjectReference"))
            {
                var full = Path.GetFullPath(Path.Combine(directory, ((string)reference.Attribute("Include")!).Replace('\\', '/')));
                pending.Push(Path.GetRelativePath(root, full).Replace('\\', '/'));
            }
        }

        return seen;
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CrowdFunding.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (CrowdFunding.slnx) was not found.");
    }
}
