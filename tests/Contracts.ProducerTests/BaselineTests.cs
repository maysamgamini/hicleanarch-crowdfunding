namespace Contracts.ProducerTests;

/// <summary>
/// CI's check that a contract change carries the version it needs: the snapshots of the base commit
/// against the snapshots in this commit, by the same rules as the snapshot test. A snapshot edited by
/// hand, or rewritten after the version was bumped once for a different change, is caught here.
/// <c>scripts/check-contracts.sh &lt;base&gt;</c> sets it up; without <c>CONTRACT_BASELINE</c> it is skipped.
/// </summary>
public class BaselineTests
{
    [Fact]
    public void Contract_changes_since_the_base_commit_follow_the_version_rules()
    {
        var baseline = Environment.GetEnvironmentVariable("CONTRACT_BASELINE");
        Assert.SkipWhen(string.IsNullOrEmpty(baseline), "CONTRACT_BASELINE is not set: scripts/check-contracts.sh <base commit> runs this check.");

        var problems = new List<string>();
        foreach (var module in Packages.ByModule.Keys)
        {
            var before = Path.Combine(baseline, Packages.RelativeSnapshotPath(module));
            if (!File.Exists(before))
            {
                continue; // a package the base commit did not have yet
            }

            var after = Packages.Stored(module);
            Assert.True(after is not null, $"{Packages.RelativeSnapshotPath(module)} was deleted.");
            problems.AddRange(ContractSnapshot.Problems(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(before))!.AsObject(), after)
                .Select(p => $"{module}.Contracts: {p}"));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
