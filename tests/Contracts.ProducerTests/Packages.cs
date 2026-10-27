using System.Reflection;
using System.Text.Json.Nodes;

using Campaigns.Contracts;

using Payments.Contracts;

using Pledges.Contracts;

namespace Contracts.ProducerTests;

/// <summary>The modules' Contracts packages, built from source, and where each keeps its contract files.</summary>
internal static class Packages
{
    public static readonly string Root = FindRoot();

    public static readonly IReadOnlyDictionary<string, Assembly> ByModule = new Dictionary<string, Assembly>
    {
        ["Campaigns"] = typeof(CampaignsModuleName).Assembly,
        ["Pledges"] = typeof(PledgesModuleName).Assembly,
        ["Payments"] = typeof(PaymentsModuleName).Assembly,
    };

    /// <summary>Set to 1 to accept a contract change on purpose: the snapshot is rewritten if the version allows it.</summary>
    public static bool Updating => Environment.GetEnvironmentVariable("UPDATE_CONTRACT_SNAPSHOTS") == "1";

    public static string ContractDirectory(string module) => Path.Combine(
        new[] { Path.Combine(Root, "src", "Services", module, $"{module}.Contracts"), Path.Combine(Root, "src", "Modules", module, $"{module}.Contracts") }
            .First(Directory.Exists),
        "contract");

    public static string SnapshotPath(string module) => Path.Combine(ContractDirectory(module), "contract.json");

    public static string RelativeSnapshotPath(string module) => Path.GetRelativePath(Root, SnapshotPath(module));

    public static JsonObject? Stored(string module) =>
        File.Exists(SnapshotPath(module)) ? JsonNode.Parse(File.ReadAllText(SnapshotPath(module)))!.AsObject() : null;

    public static JsonObject Current(string module) => ContractSnapshot.Of(ByModule[module]);

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
