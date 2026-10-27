using CrowdFunding.Testing;

[assembly: AssemblyFixture(typeof(Messaging.Tests.Aws))]

namespace Messaging.Tests;

/// <summary>One Moto server for the run.</summary>
public sealed class Aws : IAsyncLifetime
{
    public MotoBroker Broker { get; } = new();

    public async ValueTask InitializeAsync() => await Broker.Start();

    public ValueTask DisposeAsync() => Broker.DisposeAsync();
}
