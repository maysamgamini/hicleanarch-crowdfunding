using System.Text.Json.Nodes;

namespace ContractGate.Tests;

/// <summary>
/// can-i-deploy in both directions, against the running and the previous version of every other service.
/// The four services of Stage 5's end state, with the contracts and expectations checked in now.
/// </summary>
public class CanIDeployTests
{
    [Fact]
    public void A_version_that_changes_no_contract_may_deploy()
    {
        var repository = new Versions().Add("v1").Add("v2");

        var result = CanIDeploy.Check(repository, Deployed.AllAt("v1"), "pledges", "v2");

        Assert.True(result.Passed, string.Join("\n", result.Problems));
    }

    [Fact]
    public void Removing_a_field_a_deployed_consumer_reads_is_refused()
    {
        var repository = new Versions().Add("v1").Add("v2", f => f.Contract("Pledges", c => RemoveField(c, "PledgeActivated", "backerId")));

        var result = CanIDeploy.Check(repository, Deployed.AllAt("v1"), "pledges", "v2");

        Assert.Contains(result.Problems, p => p.StartsWith("notifications (running, v1): Notifications reading Pledges.PledgeActivated: backerId", StringComparison.Ordinal));
    }

    [Fact]
    public void A_consumer_that_requires_an_event_the_producer_does_not_send_waits_until_the_producers_rollback_target_sends_it_too()
    {
        // PledgeDecided (CQA-1625) as it arrived: Pledges' contract has it from v2, Notifications reads it from v3.
        // v3 is the code as checked in; the two earlier versions take the event out again.
        var repository = new Versions()
            .Add("v1", f =>
            {
                f.Contract("Pledges", c => c["events"]!.AsObject().Remove("PledgeDecided"));
                f.Expectations("Notifications", e => e["events"]!.AsObject().Remove("Pledges.PledgeDecided"));
            })
            .Add("v2", f => f.Expectations("Notifications", e => e["events"]!.AsObject().Remove("Pledges.PledgeDecided")))
            .Add("v3");

        var beforePledges = CanIDeploy.Check(repository, Deployed.AllAt("v1"), "notifications", "v3");
        var pledgesJustDeployed = CanIDeploy.Check(repository, With(Deployed.AllAt("v1"), ("pledges", "v2", "v1")), "notifications", "v3");
        var pledgesDeployedTwice = CanIDeploy.Check(repository, With(Deployed.AllAt("v1"), ("pledges", "v2", "v2")), "notifications", "v3");

        Assert.Contains(beforePledges.Problems, p => p.Contains("against pledges (running, v1): Notifications handles Pledges.PledgeDecided", StringComparison.Ordinal));
        Assert.Contains(pledgesJustDeployed.Problems, p => p.Contains("against pledges (previous, v1)", StringComparison.Ordinal));
        Assert.True(pledgesDeployedTwice.Passed, string.Join("\n", pledgesDeployedTwice.Problems));
    }

    [Fact]
    public void A_change_that_breaks_a_consumers_rollback_target_is_refused_while_it_is_the_rollback_target()
    {
        // Payments v2 no longer needs PledgeChargeRequested's currency; its rollback target v1 still does.
        var repository = new Versions()
            .Add("v1")
            .Add("v2", f => f.Expectations("Payments", e => e["events"]!["Pledges.PledgeChargeRequested"]!["requires"]!.AsObject().Remove("currency")))
            .Add("v3", f =>
            {
                f.Expectations("Payments", e => e["events"]!["Pledges.PledgeChargeRequested"]!["requires"]!.AsObject().Remove("currency"));
                f.Contract("Pledges", c => RemoveField(c, "PledgeChargeRequested", "currency"));
            });

        var whilePrevious = CanIDeploy.Check(repository, With(Deployed.AllAt("v1"), ("payments", "v2", "v1")), "pledges", "v3");
        var afterwards = CanIDeploy.Check(repository, With(Deployed.AllAt("v1"), ("payments", "v2", "v2")), "pledges", "v3");

        Assert.Contains(whilePrevious.Problems, p => p.StartsWith("payments (previous, v1): Payments reading Pledges.PledgeChargeRequested: currency", StringComparison.Ordinal));
        Assert.DoesNotContain(whilePrevious.Problems, p => p.StartsWith("payments (running", StringComparison.Ordinal));
        Assert.True(afterwards.Passed, string.Join("\n", afterwards.Problems));
    }

    [Fact]
    public void An_added_optional_field_passes_every_consumer_and_every_rollback_target()
    {
        var repository = new Versions().Add("v1").Add("v2", f => f.Contract("Pledges", c =>
            c["events"]!["PledgeChargeRequested"]!["schema"]!["properties"]!["reason"] = new JsonObject { ["type"] = new JsonArray("string", "null") }));

        var result = CanIDeploy.Check(repository, Deployed.Running(("notifications", "v1", "v1"), ("payments", "v1", "v1"), ("campaigns", "v1", "v1")), "pledges", "v2");

        Assert.True(result.Passed, string.Join("\n", result.Problems));
    }

    [Fact]
    public void The_record_must_agree_with_what_ECS_runs()
    {
        var repository = new Versions().Add("v1").Add("v2");
        var ecs = new Dictionary<string, string> { ["campaigns"] = "v1", ["notifications"] = "v1", ["payments"] = "v0", ["pledges"] = "v1" };

        var result = CanIDeploy.Check(repository, Deployed.AllAt("v1"), "pledges", "v2", ecs);

        Assert.Contains("The record says payments runs v1, ECS runs v0: fix the record before deploying", result.Problems);
    }

    [Fact]
    public void A_service_ECS_does_not_report_is_refused()
    {
        var repository = new Versions().Add("v1").Add("v2");

        var result = CanIDeploy.Check(repository, Deployed.AllAt("v1"), "pledges", "v2", new Dictionary<string, string> { ["pledges"] = "v1" });

        Assert.Contains(result.Problems, p => p.StartsWith("The record names campaigns, which ECS does not report", StringComparison.Ordinal));
    }

    [Fact]
    public void A_module_used_in_process_from_another_service_is_refused()
    {
        // Campaigns as it is checked in still sends Pledges' CancelFunding in process.
        var repository = new Versions().Add("v1").Add("v2", f => f.Expectations("Campaigns", e => e["inProcess"] = new JsonArray("Pledges.CancelFunding")));

        var result = CanIDeploy.Check(repository, Deployed.AllAt("v1"), "campaigns", "v2");

        Assert.Contains(result.Problems, p => p.Contains("Campaigns uses Pledges.CancelFunding in process, but Pledges runs in pledges", StringComparison.Ordinal));
    }

    [Fact]
    public void Modules_of_one_service_are_not_checked_against_each_other()
    {
        var oneService = """{ "services": { "monolith": ["Campaigns", "Notifications", "Pledges"], "payments": ["Payments"] } }""";
        var repository = new Versions().Add("v1", f => f.Services(oneService)).Add("v2", f => f.Services(oneService));

        var result = CanIDeploy.Check(repository, Deployed.Running(("monolith", "v1", null), ("payments", "v1", null)), "monolith", "v2");

        Assert.True(result.Passed, string.Join("\n", result.Problems));
    }

    [Fact]
    public void A_deployed_version_without_expectations_is_refused_unless_it_is_the_first_adoption()
    {
        var repository = new Versions().Add("v0", f => f.Remove(Expectations.PathOf("Notifications"))).Add("v1");
        var record = With(Deployed.AllAt("v1"), ("notifications", "v0", null));

        var refused = CanIDeploy.Check(repository, record, "pledges", "v1");
        var adopted = CanIDeploy.Check(repository, record, "pledges", "v1", firstAdoption: true);

        Assert.Contains("notifications (running, v0): Notifications has no expectations file, so nothing it reads could be checked", refused.Problems);
        Assert.True(adopted.Passed, string.Join("\n", adopted.Problems));
        Assert.Single(adopted.Notes);
    }

    [Fact]
    public void A_deployed_version_from_before_the_gate_is_refused_unless_it_is_the_first_adoption()
    {
        var repository = new Versions().Add("v0", f => f.Remove(Release.ServicesPath)).Add("v1");
        var record = With(Deployed.AllAt("v1"), ("payments", "v0", null));

        Assert.Contains("payments (running, v0): predates deploy/services.json, so nothing it reads could be checked", CanIDeploy.Check(repository, record, "pledges", "v1").Problems);
        Assert.True(CanIDeploy.Check(repository, record, "pledges", "v1", firstAdoption: true).Passed);
    }

    private static Dictionary<string, DeployedService> With(IReadOnlyDictionary<string, DeployedService> record, (string Service, string Running, string? Previous) change) =>
        record.Values.Where(d => d.Service != change.Service)
            .Append(new DeployedService(change.Service, change.Running, change.Previous, DateTimeOffset.UnixEpoch))
            .ToDictionary(d => d.Service, StringComparer.Ordinal);

    internal static void RemoveField(JsonObject contract, string eventName, string field)
    {
        var schema = contract["events"]![eventName]!["schema"]!.AsObject();
        schema["properties"]!.AsObject().Remove(field);
        var required = schema["required"]!.AsArray();
        required.Remove(required.First(n => (string)n! == field));
    }

    internal static void AddEvent(JsonObject contract, string eventName) =>
        contract["events"]![eventName] = new JsonObject
        {
            ["version"] = 1,
            ["schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["backerId"] = new JsonObject { ["format"] = "uuid", ["type"] = "string" },
                    ["campaignId"] = new JsonObject { ["format"] = "uuid", ["type"] = "string" },
                },
                ["required"] = new JsonArray("backerId", "campaignId"),
            },
        };

    internal static JsonObject Expecting(params string[] fields) => new()
    {
        ["version"] = 1,
        ["requires"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f, (JsonNode?)new JsonObject { ["format"] = "uuid", ["types"] = new JsonArray("string") }))),
    };
}
