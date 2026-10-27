using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace IntegrationTests;

/// <summary>One Valkey 8 for the caching tests, as ElastiCache runs it.</summary>
public sealed class Valkey : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("valkey/valkey:8")
        .WithPortBinding(6379, true)
        // Ready when Valkey answers, not merely when its port is open.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("valkey-cli", "ping"))
        .Build();

    /// <summary>
    /// The address, with the options the program itself uses (lesson s3-l03): a connection that fails
    /// at first keeps retrying instead of throwing, as on a busy machine or a restarting node.
    /// </summary>
    public string Configuration => $"{_container.Hostname}:{_container.GetMappedPublicPort(6379)},abortConnect=false";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
