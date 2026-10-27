using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArchitectureTests;

/// <summary>
/// Every module and host builds against the modules' Contracts as versioned packages, never as source,
/// as with the BuildingBlocks. These tests read the project files, because a project reference and a
/// package reference compile to the same assembly.
/// </summary>
public partial class ContractPackagingTests
{
    private static readonly string Root = FindRoot();

    private static readonly string[] Producers = ["Campaigns", "Pledges", "Payments"];

    public static TheoryData<string> ConsumerProjects() =>
        [.. Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("BuildingBlocks", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(Root, p))];

    public static TheoryData<string> ContractsPackages() => [.. Producers];

    [Theory]
    [MemberData(nameof(ConsumerProjects))]
    public void A_project_references_another_modules_contracts_as_a_package(string project)
    {
        var references = XDocument.Load(Path.Combine(Root, project)).Descendants("ProjectReference")
            .Select(r => (string)r.Attribute("Include")!);

        Assert.DoesNotContain(references, r => r.EndsWith(".Contracts.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void The_main_solution_lists_no_contracts_project()
    {
        var projects = XDocument.Load(Path.Combine(Root, "CrowdFunding.slnx")).Descendants("Project").Select(p => (string)p.Attribute("Path")!);

        Assert.DoesNotContain(projects, p => p.EndsWith(".Contracts.csproj", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ContractsPackages))]
    public void Each_contracts_package_is_pinned_to_its_own_version_property(string module)
    {
        var pin = XDocument.Load(Path.Combine(Root, "Directory.Packages.props")).Descendants("PackageVersion")
            .Single(p => (string)p.Attribute("Include")! == $"CrowdFunding.{module}.Contracts");

        Assert.Equal($"$({module}ContractsVersion)", (string)pin.Attribute("Version")!);
    }

    [Theory]
    [MemberData(nameof(ContractsPackages))]
    public void The_version_the_host_runs_against_is_the_version_the_contracts_declare(string module)
    {
        // A local pack is <version>-local.<time>, CI's is <version>: both are the version the project declares.
        var declared = Version.Parse(Regex.Match(File.ReadAllText(PropsOf(module)), @"<Version>(\d+\.\d+\.\d+)").Groups[1].Value);
        var loaded = System.Reflection.Assembly.Load($"{module}.Contracts").GetName().Version!;

        Assert.Equal(declared, new Version(loaded.Major, loaded.Minor, loaded.Build));
    }

    [Theory]
    [MemberData(nameof(ContractsPackages))]
    public void The_contracts_version_is_semantic(string module) =>
        Assert.Matches(SemVer(), File.ReadAllText(PropsOf(module)));

    private static string PropsOf(string module) => Path.Combine(ContractsOf(module), "Directory.Build.props");

    /// <summary>A module's Contracts: in its service's folder once it has left the monolith (s5-l05), else under src/Modules.</summary>
    private static string ContractsOf(string module) =>
        new[] { Path.Combine(Root, "src", "Services", module, $"{module}.Contracts"), Path.Combine(Root, "src", "Modules", module, $"{module}.Contracts") }
            .First(Directory.Exists);

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
