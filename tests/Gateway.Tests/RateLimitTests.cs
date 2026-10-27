using System.Net;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Gateway.Tests;

/// <summary>One Valkey for these tests, as ElastiCache runs it.</summary>
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

public sealed class RateLimitTests(Valkey valkey) : IClassFixture<Valkey>
{
    [Fact]
    public async Task Above_the_burst_a_client_gets_429_with_retry_after()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var gateway = GatewayHost.Start(monolith.Address, valkey.Configuration, perSecond: 1, burst: 3);
        var client = gateway.CreateClient();

        var codes = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            using var request = GatewayHost.From("198.51.100.1", $"/v1/campaigns/{Guid.NewGuid():N}");
            last = await client.SendAsync(request, ct);
            codes.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, (HttpStatusCode)429], codes);
        Assert.True(last!.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Two_gateway_tasks_share_one_bucket_per_client()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var first = GatewayHost.Start(monolith.Address, valkey.Configuration, perSecond: 1, burst: 4);
        await using var second = GatewayHost.Start(monolith.Address, valkey.Configuration, perSecond: 1, burst: 4);
        var clients = new[] { first.CreateClient(), second.CreateClient() };

        var allowed = 0;
        for (var i = 0; i < 8; i++)
        {
            using var request = GatewayHost.From("198.51.100.2", "/v1/campaigns/shared");
            var response = await clients[i % 2].SendAsync(request, ct);
            allowed += response.IsSuccessStatusCode ? 1 : 0;
        }

        // Two tasks with their own in-memory limiter would allow 8; one shared bucket allows 4.
        Assert.Equal(4, allowed);
    }

    [Fact]
    public async Task Each_client_has_its_own_bucket()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var gateway = GatewayHost.Start(monolith.Address, valkey.Configuration, perSecond: 1, burst: 1);
        var client = gateway.CreateClient();

        using var first = GatewayHost.From("198.51.100.4", "/v1/campaigns/a");
        using var again = GatewayHost.From("198.51.100.4", "/v1/campaigns/a");
        using var other = GatewayHost.From("198.51.100.5", "/v1/campaigns/a");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(first, ct)).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await client.SendAsync(again, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(other, ct)).StatusCode);
    }

    [Fact]
    public async Task Without_valkey_each_task_limits_its_share()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        // Nothing listens on port 1: Valkey is "down" from the start.
        await using var gateway = GatewayHost.Start(monolith.Address, "127.0.0.1:1", perSecond: 1, burst: 4, tasks: 2);
        var client = gateway.CreateClient();

        var allowed = 0;
        for (var i = 0; i < 4; i++)
        {
            using var request = GatewayHost.From("198.51.100.3", "/v1/campaigns/down");
            allowed += (await client.SendAsync(request, ct)).IsSuccessStatusCode ? 1 : 0;
        }

        Assert.Equal(2, allowed);
    }
}
