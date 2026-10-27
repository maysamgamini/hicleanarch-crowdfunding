using System.Runtime.CompilerServices;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;

using CrowdFunding.Messaging;

namespace Contracts.ProducerTests;

/// <summary>
/// A message on the broker says which version of its event it carries (the <c>event_version</c>
/// attribute, s4-l02); that number must be the one the published contract gives the event.
/// </summary>
public class EventVersionAttributeTests
{
    public static TheoryData<string, string> Events() =>
    [
        .. Packages.ByModule.SelectMany(m => m.Value.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(IntegrationEvent)) && !t.IsAbstract)
            .Select(t => (m.Key, t.FullName!))),
    ];

    [Theory]
    [MemberData(nameof(Events))]
    public void The_event_version_attribute_is_the_contracts_version(string module, string eventType)
    {
        var type = Packages.ByModule[module].GetType(eventType)!;
        var version = ((IntegrationEvent)RuntimeHelpers.GetUninitializedObject(type)).Version;
        var contracted = (int)Packages.Stored(module)!["events"]![type.Name]!["version"]!;

        var attributes = SnsEventPublisher.AttributesOf(new OutboxRecord(module, Guid.NewGuid(), type.Name, "{}", Version: version));

        Assert.Equal(contracted.ToString(System.Globalization.CultureInfo.InvariantCulture), attributes[MessageAttributes.EventVersion]);
    }
}
