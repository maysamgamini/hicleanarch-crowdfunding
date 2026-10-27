using System.Reflection;
using System.Xml.Linq;

namespace ArchitectureTests;

/// <summary>
/// Domain and Application do not know how their module is deployed: no broker, no AWS SDK, no HTTP
/// client and no host. That is what lets a module leave the monolith with only its Infrastructure,
/// its host and its wiring changed; scripts/split-diff.sh checks the same claim on the diff between
/// tags. <see cref="LayerRulesTests"/> checks references between our own assemblies only, so a
/// Domain that used the AWS SDK or HttpClient passed it. This rule is checked twice: on the compiled
/// assemblies (what the code uses) and on the project files (what a project may use before any type
/// of it shows in the metadata). Notifications is one project with its adapters inside
/// (poly-pattern-tiers), so it has no Domain or Application to check.
/// </summary>
public class DeploymentFreeLayersTests
{
    // Declared first: the fields below read it while they are initialised.
    private static readonly string[] Ours = ["Campaigns", "Pledges", "Payments", "Notifications", "BuildingBlocks"];

    private static readonly string Root = FindRoot();

    private static readonly Assembly[] Layers = [.. Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(n => IsLayer(n!))
        .Select(n => Assembly.Load(n!))];

    private static readonly string[] Projects = [.. Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
        .Where(p => IsLayer(Path.GetFileNameWithoutExtension(p)))
        .Select(p => Path.GetRelativePath(Root, p))];

    public static TheoryData<string> LayerAssemblies() => [.. Layers.Select(a => a.GetName().Name!)];

    public static TheoryData<string> LayerProjects() => [.. Projects];

    [Fact]
    public void Every_domain_and_application_layer_is_checked()
    {
        Assert.Superset(
            new HashSet<string> { "Campaigns.Domain", "Campaigns.Application", "Pledges.Domain", "Pledges.Application",
                "Payments.Domain", "Payments.Application", "BuildingBlocks.Domain", "BuildingBlocks.Application" },
            Layers.Select(a => a.GetName().Name!).ToHashSet());
        Assert.Superset(
            new HashSet<string> { "Campaigns.Domain", "Campaigns.Application", "Pledges.Domain", "Pledges.Application",
                "Payments.Domain", "Payments.Application" },
            Projects.Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet());
    }

    /// <summary>The rule would pass vacuously if its names matched nothing; the adapters and hosts show they do.</summary>
    [Fact]
    public void The_rule_recognises_what_the_adapters_and_hosts_use()
    {
        Assert.Contains(Assembly.Load("CrowdFunding.Messaging").GetReferencedAssemblies(), r => IsDeploymentConcern(r.Name!));
        Assert.Contains(Assembly.Load("CrowdFunding.Pledges.Host").GetReferencedAssemblies(), r => r.Name == "CrowdFunding.Messaging");
        Assert.True(IsDeploymentConcern("CrowdFunding.Messaging"));
        Assert.True(IsDeploymentConcern("CrowdFunding.Pledges.Host"));
        Assert.True(IsDeploymentConcern("CrowdFunding.Payments.Host"));
        Assert.True(IsDeploymentConcern("System.Net.Http"));
        Assert.True(IsDeploymentConcern("AWSSDK.SQS"));
        Assert.False(IsDeploymentConcern("BuildingBlocks.Application"));
        Assert.False(IsDeploymentConcern("Payments.Contracts"));
    }

    [Theory]
    [MemberData(nameof(LayerAssemblies))]
    public void A_domain_or_application_assembly_uses_no_broker_aws_http_client_or_host(string name)
    {
        var used = Layers.Single(a => a.GetName().Name == name).GetReferencedAssemblies().Select(r => r.Name!);

        Assert.All(used, r => Assert.False(IsDeploymentConcern(r), $"{name} references {r}"));
    }

    [Theory]
    [MemberData(nameof(LayerProjects))]
    public void A_domain_or_application_project_references_no_broker_aws_http_client_or_host(string project)
    {
        var references = XDocument.Load(Path.Combine(Root, project)).Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference")
            .Select(e => (string)e.Attribute("Include")!)
            .Select(include => include.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(include.Replace('\\', '/'))
                : include);

        Assert.All(references, r => Assert.False(IsDeploymentConcern(r), $"{project} references {r}"));
    }

    /// <summary>A Domain or Application layer of ours; packages such as Microsoft.Extensions.AmbientMetadata.Application are not.</summary>
    private static bool IsLayer(string name) =>
        name.Split('.') is [var module, "Domain" or "Application"] && Ours.Contains(module);

    /// <summary>
    /// The broker adapters (CrowdFunding.Messaging, project Messaging), the AWS SDK, HttpClient and its
    /// factory and resilience pipeline, ASP.NET Core and the generic host, and every host of ours.
    /// </summary>
    private static bool IsDeploymentConcern(string reference) =>
        reference is "Messaging" or "CrowdFunding.Messaging" or "Host" or "Polly"
        || reference.StartsWith("AWSSDK.", StringComparison.Ordinal)
        || reference.StartsWith("System.Net.Http", StringComparison.Ordinal)
        || reference.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal)
        || reference.StartsWith("Polly.", StringComparison.Ordinal)
        || reference.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
        || reference.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal)
        || reference.EndsWith(".Host", StringComparison.Ordinal);

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
