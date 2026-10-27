using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>Lesson s3-l07, built: a cached "sold out" answers the crowd; the tier's row still decides every place.</summary>
public sealed class HotTierTests(Database database, Valkey valkey) : IClassFixture<Valkey>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly WebApplicationFactory<Program> _host = database.StartHost()
        .WithWebHostBuilder(web => web.UseSetting("ConnectionStrings:Valkey", valkey.Configuration));

    public void Dispose() => _host.Dispose();

    private static string FlagKey(string publicTierId) => $"pledges:tier-sold-out:{publicTierId["tir_".Length..]}";

    private async Task<IDatabase> Valkey() => (await ConnectionMultiplexer.ConnectAsync(valkey.Configuration)).GetDatabase();

    private static async Task<HttpResponseMessage> Pledge(Scenario given, Scenario.Published campaign) =>
        await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct);

    private static async Task<string?> Reason(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString();

    [Fact]
    public async Task Taking_the_last_place_flags_the_tier_and_the_next_backer_is_refused()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 1));

        Assert.Equal(HttpStatusCode.Created, (await Pledge(given, campaign)).StatusCode);
        Assert.True(await (await Valkey()).KeyExistsAsync(FlagKey(campaign.TierIds[0])));

        var refused = await Pledge(given, campaign);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("tier_sold_out", await Reason(refused));
    }

    [Fact]
    public async Task A_flagged_tier_is_refused_before_the_database_is_asked()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var backerId = Guid.NewGuid();

        // Ten places are free, but the flag says otherwise: the answer comes from the flag alone.
        await (await Valkey()).StringSetAsync(FlagKey(campaign.TierIds[0]), "1", TimeSpan.FromSeconds(30));
        var refused = await given.Backer(backerId).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("tier_sold_out", await Reason(refused));
        Assert.Equal(0L, await given.Scalar<long>("SELECT (claimed + held)::bigint FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));

        // Without the flag, the row decides again.
        await (await Valkey()).KeyDeleteAsync(FlagKey(campaign.TierIds[0]));
        Assert.Equal(HttpStatusCode.Created, (await Pledge(given, campaign)).StatusCode);
    }

    [Fact]
    public async Task A_cancel_gives_the_place_back_and_clears_the_flag()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 1));
        var first = given.Backer(Guid.NewGuid());
        var placed = await first.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct);
        var pledgeId = (await placed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pledge_id").GetString();
        Assert.True(await (await Valkey()).KeyExistsAsync(FlagKey(campaign.TierIds[0])));

        (await first.DeleteAsync($"/v1/pledges/{pledgeId}", Ct)).EnsureSuccessStatusCode();

        Assert.False(await (await Valkey()).KeyExistsAsync(FlagKey(campaign.TierIds[0])));
        Assert.Equal(HttpStatusCode.Created, (await Pledge(given, campaign)).StatusCode);
    }

    [Fact]
    public async Task A_flag_lapses_on_its_own_so_a_place_that_came_back_unseen_is_found_again()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        await (await Valkey()).StringSetAsync(FlagKey(campaign.TierIds[0]), "1", TimeSpan.FromMilliseconds(300));
        Assert.Equal(HttpStatusCode.Conflict, (await Pledge(given, campaign)).StatusCode);

        await Task.Delay(400, Ct);

        Assert.Equal(HttpStatusCode.Created, (await Pledge(given, campaign)).StatusCode);
    }
}
