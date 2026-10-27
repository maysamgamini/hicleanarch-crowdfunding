using BuildingBlocks.Application;

namespace Contracts.ProducerTests;

/// <summary>
/// How a change written in C# reads in the snapshot: a nullable parameter with a default is an optional
/// field, a parameter without a default a required one.
/// </summary>
public class AuthoringTests
{
    [Fact]
    public void Adding_a_nullable_parameter_with_a_default_is_an_optional_field()
    {
        var problems = ContractSnapshot.Problems(Snapshot("1.0.0", typeof(V1.Charged)), Snapshot("1.1.0", typeof(WithOptionalCurrency.Charged)));

        Assert.Empty(problems);
    }

    [Fact]
    public void Adding_a_parameter_without_a_default_is_a_required_field()
    {
        var problems = ContractSnapshot.Problems(Snapshot("1.0.0", typeof(V1.Charged)), Snapshot("1.1.0", typeof(WithRequiredCurrency.Charged)));

        Assert.Contains(problems, p => p.Contains("Charged: currency added as a required field", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_a_parameter_removes_the_field()
    {
        var problems = ContractSnapshot.Problems(Snapshot("1.0.0", typeof(V1.Charged)), Snapshot("1.1.0", typeof(WithoutAmount.Charged)));

        Assert.Contains(problems, p => p.Contains("Charged: amountMinor removed", StringComparison.Ordinal));
    }

    private static System.Text.Json.Nodes.JsonObject Snapshot(string version, Type type) =>
        ContractSnapshot.Of("Sample.Contracts", Version.Parse(version), [type]);
}

/// <summary>One event in four shapes; each is <c>Charged</c>, as a type keeps its name across versions.</summary>
internal static class V1
{
    internal sealed record Charged(Guid EventId, DateTimeOffset OccurredAt, Guid PledgeId, long AmountMinor) : IntegrationEvent(EventId, OccurredAt);
}

internal static class WithOptionalCurrency
{
    internal sealed record Charged(Guid EventId, DateTimeOffset OccurredAt, Guid PledgeId, long AmountMinor, string? Currency = null)
        : IntegrationEvent(EventId, OccurredAt);
}

internal static class WithRequiredCurrency
{
    internal sealed record Charged(Guid EventId, DateTimeOffset OccurredAt, Guid PledgeId, long AmountMinor, string Currency)
        : IntegrationEvent(EventId, OccurredAt);
}

internal static class WithoutAmount
{
    internal sealed record Charged(Guid EventId, DateTimeOffset OccurredAt, Guid PledgeId) : IntegrationEvent(EventId, OccurredAt);
}
