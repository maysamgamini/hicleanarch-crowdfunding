using System.Reflection;

namespace ArchitectureTests;

/// <summary>
/// The platform (lesson s5-l01) is shared by every service, so it holds plumbing and never a domain
/// type: a rule or a concept of one module that slid into BuildingBlocks would couple every service
/// to that module's releases.
/// </summary>
public class PlatformRulesTests
{
    private static readonly string[] Platform =
        ["BuildingBlocks.Domain", "BuildingBlocks.Application", "BuildingBlocks.Infrastructure"];

    /// <summary>What the platform's Domain package may hold: results, errors and public ids, nothing about crowdfunding.</summary>
    private static readonly string[] DomainPackageTypes = ["Error", "ErrorKind", "PublicId", "Result", "Result`1"];

    /// <summary>The modules' words. A public platform type named with one of them is a domain type in the wrong place.</summary>
    private static readonly string[] DomainWords =
        ["Campaign", "Pledge", "Tier", "Funding", "Charge", "Payment", "Payout", "Settlement", "Dunning", "Ledger", "Notification", "Backer", "Creator", "Card"];

    [Fact]
    public void The_platforms_domain_package_holds_only_results_errors_and_ids()
    {
        var types = Assembly.Load("BuildingBlocks.Domain").GetExportedTypes()
            .Where(t => !t.IsNested)
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(DomainPackageTypes, types);
    }

    [Theory]
    [MemberData(nameof(PlatformAssemblies))]
    public void No_public_platform_type_is_named_after_a_modules_concept(string assembly)
    {
        var offenders = Assembly.Load(assembly).GetExportedTypes()
            .Where(t => DomainWords.Any(word => t.Name.Contains(word, StringComparison.Ordinal)))
            .Select(t => t.FullName);

        Assert.Empty(offenders);
    }

    public static TheoryData<string> PlatformAssemblies() => [.. Platform];
}
