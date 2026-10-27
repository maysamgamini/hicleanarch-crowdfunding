using CrowdFunding.Messaging;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntegrationTests;

/// <summary>
/// CQA-2115: every host of the tests knows only the topics and queues its service's OpenTofu root names
/// (<see cref="InfraBroker"/>), and with only those its start-up subscription check finds nothing missing.
/// Until this, every host got every address, which hid that Payments' task definition lacked two.
/// </summary>
public sealed class InfraBrokerTests(Database database) : IDisposable
{
    private readonly TwoHosts _hosts = new(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.Dispose();

    private static Task<IReadOnlyList<string>> Problems(IServiceProvider services) =>
        services.GetServices<IHostedService>().OfType<SubscriptionCheck>().Single().Problems(Ct);

    private static IEnumerable<string> Queues(IServiceProvider services) =>
        services.GetRequiredService<BrokerOptions>().Queues.Keys.Order(StringComparer.Ordinal);

    [Fact]
    public async Task Each_host_has_only_its_roots_addresses_and_every_subscription_it_checks()
    {
        var hosts = new Dictionary<string, IServiceProvider>
        {
            ["pledges"] = _hosts.Monolith.Services,
            ["payments"] = _hosts.Payments.Services,
            ["notifications"] = _hosts.Monolith.Notifications().Host.Services,
            ["campaigns"] = _hosts.Monolith.Campaigns().Services,
        };

        foreach (var (service, services) in hosts)
        {
            var expected = InfraBroker.Addresses(service)
                .Where(k => k.StartsWith("Events:Broker:Queues:", StringComparison.OrdinalIgnoreCase))
                .Select(k => k.Split(':')[3]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
            Assert.Equal(expected, Queues(services));
            Assert.Empty(await Problems(services));
        }
    }

    [Fact]
    public void A_payments_root_without_a_consumer_queue_would_refuse_to_start()
    {
        // What Payments' task definition gave it from s5-l07 to s5-l15: no campaigns or notifications queue.
        var broker = database.Provision().Settings
            .Where(kv => !kv.Key.StartsWith("Events:Broker:Queues:campaigns:", StringComparison.OrdinalIgnoreCase)
                && !kv.Key.StartsWith("Events:Broker:Queues:notifications:", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        using var payments = TwoHosts.PaymentsService(database, broker);

        var refused = Assert.ThrowsAny<Exception>(() => payments.Services);
        Assert.Contains("no queue for campaigns", refused.ToString(), StringComparison.Ordinal);
        Assert.Contains("no queue for notifications", refused.ToString(), StringComparison.Ordinal);
    }
}
