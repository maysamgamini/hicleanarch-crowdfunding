using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Valkey;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>
/// The program's one Valkey connection fails fast, and the PSP buckets go on at each task's share
/// while Valkey cannot be reached.
/// </summary>
public sealed class ValkeyClientTests(Database database, Valkey valkey) : IClassFixture<Valkey>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_client_settings_are_short_and_the_address_may_carry_tls_and_a_password()
    {
        var options = ValkeyRegistration.ClientOptions("valkey.internal:6379,ssl=true,password=s3cret,connectTimeout=5000");

        Assert.True(options.Ssl);
        Assert.Equal("s3cret", options.Password);
        Assert.False(options.AbortOnConnectFail);
        Assert.Equal(1_000, options.ConnectTimeout); // the program's setting, whatever the string says
        Assert.Equal(500, options.SyncTimeout);
        Assert.Equal(500, options.AsyncTimeout);
        Assert.Equal(10, options.KeepAlive);
    }

    [Fact]
    public async Task The_host_has_one_valkey_connection_made_with_those_settings()
    {
        await using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("ConnectionStrings:Valkey", valkey.Configuration));

        var connections = host.Services.GetServices<IConnectionMultiplexer>().ToArray();

        var connection = Assert.Single(connections);
        var used = ConfigurationOptions.Parse(connection.Configuration);
        Assert.Equal(500, used.AsyncTimeout);
        Assert.Equal(1_000, used.ConnectTimeout);
        Assert.IsType<ValkeyTokenBuckets>(host.Services.GetRequiredService<ITokenBuckets>());
    }

    [Fact]
    public async Task While_valkey_cannot_be_reached_a_task_takes_its_share_of_the_bucket_from_its_own_memory()
    {
        // Nothing listens on port 1: the connection never comes up.
        using var down = await ConnectionMultiplexer.ConnectAsync(ValkeyRegistration.ClientOptions("127.0.0.1:1"));
        var buckets = new ValkeyTokenBuckets(down, new DegradedTokenBuckets(4, TimeProvider.System), NullLogger<ValkeyTokenBuckets>.Instance);
        var bucket = new Bucket($"tests:{Guid.NewGuid():N}", new BucketRate(0.0001, 8)); // no refill while the test runs

        var grants = new List<TokenGrant>();
        for (var i = 0; i < 3; i++)
        {
            grants.Add(await buckets.TryTake([bucket], Ct));
        }

        // A quarter of a burst of 8: two calls go on, the third waits; never a blind call, never a stop.
        Assert.True(grants[0].Granted);
        Assert.True(grants[1].Granted);
        Assert.False(grants[2].Granted);
        Assert.True(grants[2].RetryAfter > TimeSpan.Zero);
    }
}
