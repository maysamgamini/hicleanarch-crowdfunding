using System.Text.Json.Nodes;

namespace Contracts.ProducerTests;

/// <summary>
/// Each Contracts package's snapshot (contract/contract.json, checked in and packed) is the contract.
/// The code must match it, and a change is accepted only on purpose, with the version SemVer asks for:
/// <c>UPDATE_CONTRACT_SNAPSHOTS=1 dotnet test tests/Contracts.ProducerTests</c> rewrites the snapshot,
/// and refuses to when the version does not allow the change.
/// </summary>
public class SnapshotTests
{
    public static TheoryData<string> Modules() => [.. Packages.ByModule.Keys];

    [Theory]
    [MemberData(nameof(Modules))]
    public void The_contract_matches_its_snapshot(string module)
    {
        var current = Packages.Current(module);
        var stored = Packages.Stored(module);
        if (stored is null)
        {
            Assert.True(Packages.Updating, $"{Packages.RelativeSnapshotPath(module)} is missing; write it with UPDATE_CONTRACT_SNAPSHOTS=1.");
            Write(module, current);
            return;
        }

        var problems = ContractSnapshot.Problems(stored, current);
        Assert.True(problems.Count == 0, $"{module}.Contracts: " + string.Join("\n", problems));
        if (Packages.Updating)
        {
            Write(module, current);
            return;
        }

        Assert.True(JsonNode.DeepEquals(stored, current),
            $"{module}.Contracts changed ({string.Join("; ", ContractSnapshot.Compare(stored, current))}, version {stored["version"]} -> {current["version"]}). "
            + "If that is intended, accept it with UPDATE_CONTRACT_SNAPSHOTS=1 and commit the snapshot.");
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Every_event_crossing_a_module_boundary_is_in_the_snapshot_with_its_version(string module)
    {
        var events = Packages.Current(module)["events"]!.AsObject();
        var types = Packages.ByModule[module].GetExportedTypes().Where(t => t.IsSubclassOf(typeof(BuildingBlocks.Application.IntegrationEvent)));

        Assert.All(types, t =>
        {
            Assert.True(events.ContainsKey(t.Name), $"{t.Name} is not in the snapshot.");
            Assert.Equal(ContractSnapshot.VersionOf(t), (int)events[t.Name]!["version"]!);
        });
    }

    [Fact]
    public void The_card_setup_command_is_pinned_as_Payments_internal_endpoint()
    {
        var endpoint = Packages.Current("Payments")["endpoints"]!["POST /internal/payments/card-setups"];

        Assert.NotNull(endpoint);
        Assert.Equal("StartCardSetup", (string)endpoint["command"]!);
        Assert.Equal(["backerId", "pledgeId"], endpoint["request"]!["required"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["cardSetupId", "clientSecret"], endpoint["response"]!["required"]!.AsArray().Select(n => (string)n!));
    }

    private static void Write(string module, JsonObject snapshot)
    {
        Directory.CreateDirectory(Packages.ContractDirectory(module));
        File.WriteAllText(Packages.SnapshotPath(module), ContractSnapshot.Write(snapshot));
    }
}
