using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace IntegrationTests;

/// <summary>One Valkey 8 for the caching tests, as ElastiCache runs it.</summary>
public sealed class Valkey : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("valkey/valkey:8")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
        .Build();

    public string Configuration => $"{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
