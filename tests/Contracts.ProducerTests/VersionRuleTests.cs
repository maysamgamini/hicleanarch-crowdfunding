using System.Text.Json.Nodes;

namespace Contracts.ProducerTests;

/// <summary>
/// The rules the snapshot test applies, on the real Payments and Campaigns contracts changed by hand:
/// what a producer may do within a major version, and what takes a new one.
/// </summary>
public class VersionRuleTests
{
    [Fact]
    public void A_removed_field_fails_the_snapshot_test()
    {
        var (before, after) = Change("Payments", "1.1.0");
        Remove(Schema(after, "PledgeChargeSucceeded"), "amountMinor");

        var problem = Assert.Single(ContractSnapshot.Problems(before, after), p => p.StartsWith("Breaking", StringComparison.Ordinal));
        Assert.Contains("PledgeChargeSucceeded: amountMinor removed", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_renamed_field_fails_the_snapshot_test()
    {
        var (before, after) = Change("Payments", "1.1.0");
        var schema = Schema(after, "PledgeChargeSucceeded");
        var amount = schema["properties"]!["amountMinor"]!.DeepClone();
        Remove(schema, "amountMinor");
        schema["properties"]!.AsObject()["amount"] = amount;
        schema["required"]!.AsArray().Add("amount");

        var problems = string.Join("\n", ContractSnapshot.Problems(before, after));
        Assert.Contains("amountMinor removed", problems, StringComparison.Ordinal);
        Assert.Contains("amount added as a required field", problems, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_required_field_fails_the_snapshot_test()
    {
        var (before, after) = Change("Payments", "1.1.0");
        var schema = Schema(after, "PledgeChargeSucceeded");
        schema["properties"]!.AsObject()["currency"] = new JsonObject { ["type"] = "string" };
        schema["required"]!.AsArray().Add("currency");

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.Contains("currency added as a required field", StringComparison.Ordinal));
    }

    [Fact]
    public void Making_an_optional_field_required_fails_the_snapshot_test()
    {
        var (before, after) = Change("Payments", "1.1.0");
        Schema(after, "PledgeChargeFailed")["required"]!.AsArray().Add("retriedOnItsOwn");

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.Contains("retriedOnItsOwn now required", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_type_fails_the_snapshot_test()
    {
        var (before, after) = Change("Payments", "1.1.0");
        Schema(after, "PledgeChargeSucceeded")["properties"]!["attemptNo"]!["type"] = "string";

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.Contains("attemptNo: type", StringComparison.Ordinal));
    }

    [Fact]
    public void A_field_removed_from_a_nested_object_fails_the_snapshot_test()
    {
        var (before, after) = Change("Campaigns", "1.1.0");
        Remove(Schema(after, "CampaignPublished")["properties"]!["tiers"]!["items"]!.AsObject(), "limitQty");

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.Contains("tiers[].limitQty removed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_removed_event_fails_the_snapshot_test()
    {
        var (before, after) = Change("Payments", "1.1.0");
        after["events"]!.AsObject().Remove("CampaignSettled");

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.Contains("CampaignSettled: event removed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_field_removed_from_the_endpoint_reply_fails_the_snapshot_test()
    {
        var (before, after) = Change("Payments", "1.1.0");
        Remove(after["endpoints"]!["POST /internal/payments/card-setups"]!["response"]!.AsObject(), "clientSecret");

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.Contains("POST /internal/payments/card-setups: response.clientSecret removed", StringComparison.Ordinal));
    }

    [Fact]
    public void An_added_optional_field_passes_with_a_new_minor_version()
    {
        var (before, after) = Change("Payments", "1.1.0");
        AddOptionalCurrency(Schema(after, "PledgeChargeSucceeded"));

        Assert.Empty(ContractSnapshot.Problems(before, after));
        Assert.Equal("PledgeChargeSucceeded: currency added as an optional field", Assert.Single(ContractSnapshot.Compare(before, after)).ToString());
    }

    [Fact]
    public void An_added_optional_field_without_a_version_bump_fails()
    {
        var (before, after) = Change("Payments", "1.0.1");
        AddOptionalCurrency(Schema(after, "PledgeChargeSucceeded"));

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.StartsWith("Additions need at least a new minor version (1.1.0)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_event_passes_with_a_new_minor_version()
    {
        var (before, after) = Change("Payments", "1.1.0");
        after["events"]!.AsObject()["PayoutSent"] = before["events"]!["CampaignSettled"]!.DeepClone();

        Assert.Empty(ContractSnapshot.Problems(before, after));
    }

    [Fact]
    public void A_breaking_change_passes_only_with_a_new_major_version_and_a_higher_event_version()
    {
        var (before, after) = Change("Payments", "2.0.0");
        Remove(Schema(after, "PledgeChargeSucceeded"), "amountMinor");

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.StartsWith("PledgeChargeSucceeded changed in a breaking way but still carries version 1", StringComparison.Ordinal));

        after["events"]!["PledgeChargeSucceeded"]!["version"] = 2;
        Assert.Empty(ContractSnapshot.Problems(before, after));
    }

    [Fact]
    public void A_higher_event_version_alone_is_a_breaking_change()
    {
        var (before, after) = Change("Payments", "1.1.0");
        after["events"]!["PledgeChargeSucceeded"]!["version"] = 2;

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.Contains("version 1 -> 2", StringComparison.Ordinal));
    }

    [Fact]
    public void The_version_never_goes_down()
    {
        var (before, after) = Change("Payments", "0.9.0");

        Assert.Contains(ContractSnapshot.Problems(before, after), p => p.StartsWith("The version went down", StringComparison.Ordinal));
    }

    private static (JsonObject Before, JsonObject After) Change(string module, string version)
    {
        var before = Packages.Current(module);
        var after = before.DeepClone().AsObject();
        after["version"] = version;
        return (before, after);
    }

    private static JsonObject Schema(JsonObject snapshot, string eventType) => snapshot["events"]![eventType]!["schema"]!.AsObject();

    private static void Remove(JsonObject schema, string field)
    {
        schema["properties"]!.AsObject().Remove(field);
        if (schema["required"] is JsonArray required)
        {
            required.Remove(required.First(n => (string)n! == field));
        }
    }

    private static void AddOptionalCurrency(JsonObject schema) =>
        schema["properties"]!.AsObject()["currency"] = new JsonObject { ["default"] = null, ["type"] = new JsonArray("string", "null") };
}
