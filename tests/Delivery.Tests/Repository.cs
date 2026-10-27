using System.Text.Json;
using System.Xml.Linq;

namespace Delivery.Tests;

/// <summary>The repository's own files, read as data: the solution, its filters and the project graph.</summary>
internal static class Repository
{
    public static readonly string Root = FindRoot();

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    /// <summary>Every project of CrowdFunding.slnx (or another solution), as repository paths with forward slashes.</summary>
    public static IReadOnlySet<string> SolutionProjects(string solution = "CrowdFunding.slnx") =>
        XDocument.Load(Path.Combine(Root, solution)).Descendants("Project")
            .Select(p => Normalize((string)p.Attribute("Path")!))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The solution a filter (.slnf) narrows: CrowdFunding.slnx, or Contracts.slnx for the Contracts (s4-l06).</summary>
    public static string FilterSolution(string filter)
    {
        using var document = JsonDocument.Parse(Read(filter));
        return document.RootElement.GetProperty("solution").GetProperty("path").GetString()!;
    }

    /// <summary>The projects a solution filter (.slnf) builds.</summary>
    public static IReadOnlySet<string> FilterProjects(string filter)
    {
        using var document = JsonDocument.Parse(Read(filter));
        return document.RootElement.GetProperty("solution").GetProperty("projects").EnumerateArray()
            .Select(p => Normalize(p.GetString()!))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The given projects and every project they reference, directly or through another.</summary>
    public static IReadOnlySet<string> Closure(IEnumerable<string> projects)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(projects);
        while (pending.TryPop(out var project))
        {
            if (!seen.Add(project))
            {
                continue;
            }

            foreach (var reference in References(project))
            {
                pending.Push(reference);
            }
        }

        return seen;
    }

    /// <summary>The project references a project file declares, resolved to repository paths.</summary>
    public static IEnumerable<string> References(string project)
    {
        var directory = Path.GetDirectoryName(Path.Combine(Root, project))!;
        return XDocument.Load(Path.Combine(Root, project)).Descendants("ProjectReference")
            .Select(r => Path.GetFullPath(Path.Combine(directory, Normalize((string)r.Attribute("Include")!))))
            .Select(full => Normalize(Path.GetRelativePath(Root, full)));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string FindRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CrowdFunding.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return root;
    }
}
