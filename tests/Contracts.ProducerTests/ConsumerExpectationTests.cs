using System.Text.Json.Nodes;

using ContractGate;

namespace Contracts.ProducerTests;

/// <summary>
/// The producer's pipeline checks every consumer pair before anything is packed: each module's
/// expectations file, written by the consumer side's tests, against the contract the producer's source
/// gives now. A change that a consumer cannot read fails here, in the producer's build, whatever its version.
/// </summary>
public class ConsumerExpectationTests
{
    public static TheoryData<string, string> Pairs()
    {
        var pairs = new TheoryData<string, string>();
        foreach (var consumer in Consumers().Keys)
        {
            foreach (var producer in Packages.ByModule.Keys.Where(p => p != consumer))
            {
                pairs.Add(producer, consumer);
            }
        }

        return pairs;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void The_producers_contract_gives_the_consumer_what_it_expects(string producer, string consumer)
    {
        var problems = Compatibility.Problems(Consumers()[consumer], producer, Packages.Current(producer));

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_module_that_reads_another_has_an_expectations_file() =>
        Assert.Equal(["Campaigns", "Notifications", "Payments", "Pledges"], Consumers().Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Removing_a_field_a_consumer_reads_fails_the_producer()
    {
        var contract = Packages.Current("Pledges");
        var schema = contract["events"]!["PledgeActivated"]!["schema"]!.AsObject();
        schema["properties"]!.AsObject().Remove("backerId");
        Remove(schema["required"]!.AsArray(), "backerId");

        var problems = Compatibility.Problems(Consumers()["Notifications"], "Pledges", contract);

        Assert.Contains(problems, p => p.Contains("Notifications reading Pledges.PledgeActivated: backerId", StringComparison.Ordinal));
    }

    [Fact]
    public void Making_a_field_optional_that_a_consumer_requires_fails_the_producer()
    {
        var contract = Packages.Current("Payments");
        Remove(contract["events"]!["PaymentMethodSaved"]!["schema"]!["required"]!.AsArray(), "pledgeId");

        Assert.Contains(Compatibility.Problems(Consumers()["Pledges"], "Payments", contract), p => p.Contains("pledgeId is required by the reader and not always sent", StringComparison.Ordinal));
    }

    [Fact]
    public void Raising_an_event_version_a_consumer_reads_fails_the_producer()
    {
        var contract = Packages.Current("Campaigns");
        contract["events"]!["CampaignPublished"]!["version"] = 2;

        Assert.Contains(Compatibility.Problems(Consumers()["Payments"], "Campaigns", contract), p => p.Contains("version 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_an_optional_field_passes_every_consumer()
    {
        var contract = Packages.Current("Pledges");
        contract["events"]!["PledgeActivated"]!["schema"]!["properties"]!["note"] = new JsonObject { ["type"] = new JsonArray("string", "null") };

        Assert.All(Consumers().Values, c => Assert.Empty(Compatibility.Problems(c, "Pledges", contract)));
    }

    private static Dictionary<string, Expectations> Consumers() =>
        Directory.GetFiles(Path.Combine(Packages.Root, "src", "Modules"), "expectations.json", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Packages.Root, "src", "Services"), "expectations.json", SearchOption.AllDirectories))
            .Select(f => Expectations.Read(File.ReadAllText(f)))
            .ToDictionary(e => e.Module, StringComparer.Ordinal);

    private static void Remove(JsonArray names, string name) =>
        names.Remove(names.First(n => (string)n! == name));
}
