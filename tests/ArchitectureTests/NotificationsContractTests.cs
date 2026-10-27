using System.Reflection;

using BuildingBlocks.Application;

namespace ArchitectureTests;

/// <summary>Notifications only tells people: no event it consumes carries a backer's payment data (CQA-1625).</summary>
public sealed class NotificationsContractTests
{
    [Fact]
    public void No_event_Notifications_handles_carries_a_payment_method()
    {
        var handled = typeof(Notifications.NotificationsModule).Assembly.GetTypes()
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct()
            .ToList();

        Assert.NotEmpty(handled);
        var carrying = handled
            .SelectMany(e => e.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Event: e.Name, Property: p.Name)))
            .Where(p => p.Property.Contains("PaymentMethod", StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.Event}.{p.Property}")
            .ToList();
        Assert.Empty(carrying);
    }
}
