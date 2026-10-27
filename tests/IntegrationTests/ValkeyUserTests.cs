using System.Net;
using System.Net.Http.Json;

using BuildingBlocks.Application;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Payments.Application;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l01: each service signs in to Valkey as its own user, declared in infra/env/valkey.tf. The
/// platform's connection comes up as that user, the service's real cache, flag and bucket calls work
/// through it, and another service's key is refused with NOPERM.
/// </summary>
public sealed class ValkeyUserTests(Database database) : IAsyncLifetime
{
    private const string ApiPassword = "api-test-only-password";
    private const string PaymentsPassword = "payments-test-only-password";

    private readonly IContainer _valkey = new ContainerBuilder("valkey/valkey:8")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("valkey-cli", "ping"))
        .Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => $"{_valkey.Hostname}:{_valkey.GetMappedPublicPort(6379)}";

    public async ValueTask InitializeAsync()
    {
        await _valkey.StartAsync(Ct);
        await using var admin = await ConnectionMultiplexer.ConnectAsync(Address + ",abortConnect=false");
        await ValkeyUser.Create(admin, "api", ApiPassword);
        await ValkeyUser.Create(admin, "payments", PaymentsPassword);
    }

    public ValueTask DisposeAsync() => _valkey.DisposeAsync();

    [Fact]
    public async Task Each_service_works_through_its_own_user_and_is_refused_the_others_keys()
    {
        var asApi = ValkeyUser.ConnectionString(Address, "api", ApiPassword);
        var asPayments = ValkeyUser.ConnectionString(Address, "payments", PaymentsPassword);
        await using var host = database.StartHost(payments: web => web.UseSetting("ConnectionStrings:Valkey", asPayments))
            .WithWebHostBuilder(web => web.UseSetting("ConnectionStrings:Valkey", asApi));
        var given = new Scenario(database, host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 1));
        var reader = host.CreateClient();
        await using var admin = await ConnectionMultiplexer.ConnectAsync(Address + ",abortConnect=false");

        // The monolith, as api: the catalog cache with its refill lock, the totals, a sold-out flag.
        (await reader.GetAsync($"/v1/campaigns/{campaign.Id}", Ct)).EnsureSuccessStatusCode();
        await Scenario.Eventually(async () => await admin.GetDatabase().KeyExistsAsync($"campaigns:catalog:{campaign.Id}"));
        Assert.Equal(HttpStatusCode.Created,
            (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct)).StatusCode);
        await Scenario.Eventually(async () => await admin.GetDatabase().KeyExistsAsync($"pledges:totals:{campaign.CampaignId:N}"));
        await Scenario.Eventually(async () => await admin.GetDatabase().KeyExistsAsync($"pledges:tier-sold-out:{Guid.ParseExact(campaign.TierIds[0]["tir_".Length..], "N"):N}"));
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/health/ready", Ct)).StatusCode);

        // Payments, as payments: the PSP budget's real buckets grant a token.
        var payments = host.Payments().Services;
        var budget = payments.GetRequiredService<PspBudget>();
        Assert.True((await payments.GetRequiredService<ITokenBuckets>().TryTake(budget.For(PspCall.Charge), Ct)).Granted);
        Assert.True(await admin.GetDatabase().KeyExistsAsync("buckets:psp:charges"));

        await Refused(asApi, "buckets:psp:charges", "payments:pause", "gateway:limits:x");
        // Nor may the monolith list or wipe what the others keep (CQA-1516's rule, now for api too).
        await using (var api = await ConnectionMultiplexer.ConnectAsync(asApi + ",allowAdmin=true"))
        {
            foreach (var (command, args) in new (string, object[])[] { ("FLUSHALL", []), ("FLUSHDB", []), ("KEYS", ["*"]), ("SCAN", ["0"]) })
            {
                var denied = await Assert.ThrowsAsync<RedisServerException>(() => api.GetDatabase().ExecuteAsync(command, args));
                Assert.StartsWith("NOPERM", denied.Message, StringComparison.Ordinal);
            }
        }
        await Refused(asPayments, $"campaigns:catalog:{campaign.Id}", $"pledges:totals:{campaign.CampaignId:N}", "gateway:limits:x");
    }

    private static async Task Refused(string user, params string[] keys)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(user);
        foreach (var key in keys)
        {
            var denied = await Assert.ThrowsAsync<RedisServerException>(() => connection.GetDatabase().StringGetAsync(key));
            Assert.StartsWith("NOPERM", denied.Message, StringComparison.Ordinal);
        }
    }
}
