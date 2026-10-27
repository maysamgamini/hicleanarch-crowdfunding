using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArchitectureTests;

/// <summary>
/// The modules and the host use the BuildingBlocks as a versioned package, never as source. These
/// tests read the project files themselves, because a project reference and a package reference
/// compile to the same assembly.
/// </summary>
public partial class PackagingTests
{
    private static readonly string Root = FindRoot();

    public static TheoryData<string> ConsumerProjects() =>
        [.. Directory.GetFiles(Path.Combine(Root, "src", "Host"), "*.csproj")
            .Concat(Directory.GetFiles(Path.Combine(Root, "src", "Modules"), "*.csproj", SearchOption.AllDirectories))
            .Select(p => Path.GetRelativePath(Root, p))];

    [Theory]
    [MemberData(nameof(ConsumerProjects))]
    public void A_consumer_references_the_building_blocks_as_a_package(string project)
    {
        var references = XDocument.Load(Path.Combine(Root, project)).Descendants("ProjectReference")
            .Select(r => (string)r.Attribute("Include")!);

        Assert.DoesNotContain(references, r => r.Contains("BuildingBlocks", StringComparison.Ordinal));
    }

    [Fact]
    public void The_main_solution_does_not_build_the_building_blocks_from_source() =>
        Assert.DoesNotContain("src/BuildingBlocks/", File.ReadAllText(Path.Combine(Root, "CrowdFunding.slnx")), StringComparison.Ordinal);

    [Fact]
    public void Every_building_block_is_pinned_to_the_one_version_property()
    {
        var pins = XDocument.Load(Path.Combine(Root, "Directory.Packages.props")).Descendants("PackageVersion")
            .Where(p => ((string)p.Attribute("Include")!).StartsWith("CrowdFunding.BuildingBlocks.", StringComparison.Ordinal))
            .Select(p => (string)p.Attribute("Version")!)
            .ToList();

        Assert.Equal(3, pins.Count);
        Assert.All(pins, pin => Assert.Equal("$(BuildingBlocksVersion)", pin));
    }

    [Fact]
    public void The_version_the_tests_run_against_is_the_building_blocks_version()
    {
        // A local pack is 1.0.0-local.<time>, CI's is 1.0.0: both are the version the BuildingBlocks declare.
        var declared = Version.Parse(Regex.Match(
            File.ReadAllText(Path.Combine(Root, "src", "BuildingBlocks", "Directory.Build.props")), @"<Version>(\d+\.\d+\.\d+)").Groups[1].Value);
        var loaded = typeof(BuildingBlocks.Domain.Error).Assembly.GetName().Version!;

        Assert.Equal(declared, new Version(loaded.Major, loaded.Minor, loaded.Build));
    }

    [Fact]
    public void The_package_version_is_semantic() =>
        Assert.Matches(SemVer(), File.ReadAllText(Path.Combine(Root, "src", "BuildingBlocks", "Directory.Build.props")));

    [GeneratedRegex(@"<Version>\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?</Version>")]
    private static partial Regex SemVer();

    private static string FindRoot()
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
