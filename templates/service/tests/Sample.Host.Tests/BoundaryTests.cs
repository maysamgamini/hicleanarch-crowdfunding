using System.Xml.Linq;

namespace Sample.Host.Tests;

/// <summary>
/// The Sample service composes its own module and nothing of the others: another module reaches it
/// only through events and that module's Contracts. Checked on the project file, where the dependency
/// is declared, and on what the build puts beside the host.
/// </summary>
public class BoundaryTests
{
    private static readonly string[] OtherModules = [.. new[] { "Campaigns", "Pledges", "Payments", "Notifications" }.Where(m => m != "Sample")];

    [Fact]
    public void The_host_references_only_its_own_module()
    {
        var references = ProjectReferences("src/Services/Sample.Host/Sample.Host.csproj");

        Assert.NotEmpty(references);
        // Its own module, and the platform's broker transport (Messaging), which knows no module.
        Assert.All(references, r => Assert.True(r == "Sample" || r.StartsWith("Sample.", StringComparison.Ordinal) || r == "Messaging",
            $"The host references {r}"));
    }

    [Fact]
    public void Of_the_other_modules_only_their_contracts_ship_with_the_host()
    {
        var shipped = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => OtherModules.Any(m => n == m || n!.StartsWith(m + ".", StringComparison.Ordinal)));

        Assert.All(shipped, n => Assert.EndsWith(".Contracts", n, StringComparison.Ordinal));
    }

    private static string[] ProjectReferences(string relativePath)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CrowdFunding.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return [.. XDocument.Load(Path.Combine(root, relativePath))
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))];
    }
}
