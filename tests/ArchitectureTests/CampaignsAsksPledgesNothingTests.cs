using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using BuildingBlocks.Application;

namespace ArchitectureTests;

/// <summary>
/// Lesson s5-l08: before Campaigns runs in a host of its own, it sends Pledges no command and asks it no
/// query. It only reads Pledges' events. Read from the compiled assemblies' type references, so a call made
/// through any API fails here.
/// </summary>
public sealed class CampaignsAsksPledgesNothingTests
{
    private static readonly Assembly PledgesContracts = typeof(Pledges.Contracts.FundingCancelled).Assembly;

    public static TheoryData<string> CampaignsAssemblies() =>
        ["Campaigns.Domain", "Campaigns.Application", "Campaigns.Infrastructure", "Campaigns.Contracts"];

    [Theory]
    [MemberData(nameof(CampaignsAssemblies))]
    public void Campaigns_references_no_command_or_query_of_Pledges(string name)
    {
        var location = Assembly.Load(name).Location;
        using var stream = File.OpenRead(location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var asked = metadata.TypeReferences
            .Select(handle => metadata.GetTypeReference(handle))
            .Select(reference => PledgesContracts.GetType($"{metadata.GetString(reference.Namespace)}.{metadata.GetString(reference.Name)}"))
            .Where(type => type is not null && IsAsking(type))
            .Select(type => type!.FullName)
            .ToList();

        Assert.Empty(asked);
    }

    [Fact]
    public void The_rule_sees_the_ask_it_forbids() =>
        Assert.True(IsAsking(typeof(Pledges.Contracts.CancelFunding)) && IsAsking(typeof(Pledges.Contracts.ICampaignTotals)));

    /// <summary>A command or a query, or an in-process way to ask (an interface Pledges implements for others).</summary>
    private static bool IsAsking(Type type) =>
        type.IsInterface
        || type.GetInterfaces().Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICommand<>) || i.GetGenericTypeDefinition() == typeof(IQuery<>)));
}
