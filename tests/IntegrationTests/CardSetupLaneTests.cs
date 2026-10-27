using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Valkey;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>Lesson s3-l07, built: new-card backers keep their pledge and its place, and wait their turn for the card form.</summary>
public sealed class CardSetupLaneTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The lane's clock is the test's: the bucket refills only when the test moves it on, so "full"
    // and "your turn" happen when the test says, however slow the machine (the Valkey buckets'
    // timing has tests of its own, in TokenBucketTests).
    private readonly ManualClock _laneClock = new(DateTimeOffset.UtcNow);

    private readonly WebApplicationFactory<Program> _host;

    private readonly Database _database;

    public CardSetupLaneTests(Database database)
    {
        _database = database;
        _host = database.StartHost(payments: web => web.ConfigureTestServices(services => services.AddSingleton<ITokenBuckets>(new InProcessTokenBuckets(_laneClock))))
            .WithWebHostBuilder(web => web.UseSetting("Payments:Budget:CardSetupsPerSecond", "0.5")); // one card setup every two seconds
    }

    public void Dispose() => _host.Dispose();

    private static object NewCard(Scenario.Published campaign) =>
        new { amount_minor = 4_900, tier_id = campaign.TierIds[0], payment_method_id = (string?)null };

    [Fact]
    public async Task When_the_lane_is_full_the_pledge_and_its_place_are_kept_and_the_card_form_comes_later()
    {
        var given = new Scenario(_database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));

        var first = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard(campaign), Ct);
        var firstSetup = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setup");
        Assert.NotNull(firstSetup.GetProperty("client_secret").GetString());

        var backer = given.Backer(Guid.NewGuid());
        var second = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard(campaign), Ct);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("PENDING_SETUP", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("setup").GetProperty("client_secret").ValueKind);
        Assert.InRange(body.GetProperty("setup").GetProperty("retry_after_seconds").GetInt32(), 1, 2);
        Assert.Equal(2L, await given.Scalar<long>("SELECT held::bigint FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));

        var pledgeId = body.GetProperty("pledge_id").GetString();
        var tooSoon = await backer.PostAsync($"/v1/pledges/{pledgeId}/card-setup", null, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.Equal("card_setup_lane_full", (await tooSoon.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        var wait = tooSoon.Headers.RetryAfter!.Delta!.Value;
        Assert.InRange(wait, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        _laneClock.Advance(wait);
        var turn = await backer.PostAsync($"/v1/pledges/{pledgeId}/card-setup", null, Ct);

        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        Assert.NotNull((await turn.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setup").GetProperty("client_secret").GetString());
    }

    [Fact]
    public async Task Only_the_backer_of_a_waiting_pledge_can_ask_for_its_card_form()
    {
        var given = new Scenario(_database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct);
        var pledgeId = (await placed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pledge_id").GetString();

        var someoneElse = await given.Backer(Guid.NewGuid()).PostAsync($"/v1/pledges/{pledgeId}/card-setup", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, someoneElse.StatusCode);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
