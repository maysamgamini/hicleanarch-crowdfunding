namespace ArchitectureTests;

/// <summary>
/// The time fence (lesson s3-l05) needs every request-path transaction to keep the two-second
/// transaction_timeout that migrate sets on pledges_app. PostgreSQL lets any session change it (its
/// context is "user"), so the database cannot hold the module to it: the code must never touch it.
/// Only the places below may name it: the role setting, the deploy check, the check on the fence's
/// read, and the BuildingBlocks that apply role settings and know its SQLSTATE.
/// </summary>
public class TimeFenceRulesTests
{
    private static readonly string Root = FindRoot();

    private static readonly string[] Scanned = ["src", "infra"];

    private static readonly string[] Allowed =
    [
        "src/Modules/Pledges/Pledges.Infrastructure/PledgesModule.cs",
        "src/Modules/Pledges/Pledges.Infrastructure/Persistence/PledgesMigrator.cs",
        "src/Modules/Pledges/Pledges.Infrastructure/Persistence/TimeFencedFundingWindows.cs",
        "src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/DatabaseBusy.cs",
        "src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/ModuleDatabase.cs",
    ];

    [Fact]
    public void Only_the_role_setting_and_its_checks_name_transaction_timeout()
    {
        var naming = Scanned
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(Root, dir), "*", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal)
                && Path.GetExtension(f) is ".cs" or ".json" or ".tf" or ".sql")
            .Where(f => File.ReadAllText(Path.Combine(Root, f)).Contains("transaction_timeout", StringComparison.OrdinalIgnoreCase));

        Assert.All(naming, f => Assert.Contains(f, Allowed));
    }

    [Fact]
    public void The_rule_sees_the_role_setting() =>
        Assert.Contains("transaction_timeout", File.ReadAllText(Path.Combine(Root, Allowed[0])), StringComparison.Ordinal);

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
