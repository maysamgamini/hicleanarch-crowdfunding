using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Payments.Application;

using Pledges.Application;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>The campaign page through the real host, with the cache on.</summary>
public sealed class CatalogCacheTests(Database database, Valkey valkey) : IClassFixture<Valkey>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_catalog_is_cached_and_publishing_drops_the_entry()
    {
        await using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("ConnectionStrings:Valkey", valkey.Configuration));
        var scenario = new Scenario(database, host);
        var id = await scenario.Draft();
        var reader = Database.Client(host, Database.Token(scenario.CreatorId, BuildingBlocks.Infrastructure.Identity.Roles.Creator));

        var draft = await reader.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", Ct);
        Assert.Equal("DRAFT", draft.GetProperty("status").GetString());
        using var connection = await ConnectionMultiplexer.ConnectAsync(valkey.Configuration);
        Assert.True(await connection.GetDatabase().KeyExistsAsync($"campaigns:catalog:{id}"));

        (await scenario.Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct)).EnsureSuccessStatusCode();

        // The event arrives after the commit and drops the entry; the next read loads LIVE.
        await Scenario.Eventually(async () => !await connection.GetDatabase().KeyExistsAsync($"campaigns:catalog:{id}"));
        var live = await reader.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", Ct);
        Assert.Equal("LIVE", live.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_total_is_never_served_from_the_cache()
    {
        await using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("ConnectionStrings:Valkey", valkey.Configuration));
        var scenario = new Scenario(database, host);
        var published = await scenario.Publish();
        var reader = host.CreateClient();
        await reader.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{published.Id}", Ct);

        // A pledge after the catalog was cached: the total follows within the aggregator's refresh
        // (lesson s3-l05), while the catalog entry stays cached.
        (await scenario.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{published.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();

        await Scenario.Eventually(async () =>
            (await reader.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{published.Id}", Ct)).GetProperty("pledged_minor").GetInt64() == 6_000);
        using var connection = await ConnectionMultiplexer.ConnectAsync(valkey.Configuration);
        Assert.True(await connection.GetDatabase().KeyExistsAsync($"campaigns:catalog:{published.Id}"));
    }

    [Fact]
    public async Task Every_status_change_reaches_the_cached_page()
    {
        await using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("ConnectionStrings:Valkey", valkey.Configuration));
        var given = new Scenario(database, host);
        var funded = await given.Publish(1_000);
        var cancelled = await given.Publish(1_000);
        var reader = host.CreateClient();
        async Task<string?> Status(Scenario.Published c) =>
            (await reader.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{c.Id}", Ct)).GetProperty("status").GetString();

        Assert.Equal("LIVE", await Status(funded)); // both pages are now cached
        Assert.Equal("LIVE", await Status(cancelled));

        // FundingCancelled: the creator's cancel, accepted by Pledges.
        (await given.Creator().PostAsync($"/v1/campaigns/{cancelled.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();
        await Scenario.Eventually(async () => await Status(cancelled) == "CANCELLED");

        // CampaignFundingDecided, then CampaignSettled.
        (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{funded.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() WHERE campaign_id = @id", funded.CampaignId);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(funded.CampaignId), Ct);
        }

        await Scenario.Eventually(async () => await Status(funded) == "FUNDED");
        // The charge request comes through the broker as a message of its own, not with the decision (s4-l02).
        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", funded.CampaignId) == 1
            && await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id", funded.CampaignId) == 1);
        await using (var scope = host.Payments().Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<RunSettlement, bool>(new RunSettlement("cache-test", funded.CampaignId), Ct);
        }

        await Scenario.Eventually(async () => await Status(funded) == "SETTLED");
    }
}
