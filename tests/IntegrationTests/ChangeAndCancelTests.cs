using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IntegrationTests;

/// <summary>Changes and cancels keep the total exact, and nothing moves after the deadline.</summary>
public sealed class ChangeAndCancelTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private static async Task<string> Place(HttpClient backer, Scenario.Published campaign, long amount, string? tier = null)
    {
        var placed = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(amount, tier), Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        return placed.Headers.Location!.ToString();
    }

    private static Task<HttpResponseMessage> Change(HttpClient backer, string pledge, long amount, string? tier = null) =>
        backer.PatchAsJsonAsync(pledge, new { amount_minor = amount, tier_id = tier }, Ct);

    private static async Task<string> Reason(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString()!;

    private static async Task AssertTotalIsTheLedger(Scenario given, Guid campaignId)
    {
        var snapshot = await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaignId);
        var ledger = await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaignId);
        var active = await given.Scalar<long>("SELECT coalesce(sum(amount_minor), 0)::bigint FROM pledges.pledges WHERE campaign_id = @id AND status = 'ACTIVE'", campaignId);
        var backers = await given.Scalar<int>("SELECT backers FROM pledges.funding_windows WHERE campaign_id = @id", campaignId);
        var live = await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id AND status = 'ACTIVE'", campaignId);
        Assert.Equal(ledger, snapshot);
        Assert.Equal(active, snapshot);
        Assert.Equal(live, backers);
    }

    [Fact]
    public async Task The_total_equals_the_ledger_sum_after_any_sequence()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 2), ("Founder", 9_900, null));
        var (anna, ben, cleo) = (given.Backer(Guid.NewGuid()), given.Backer(Guid.NewGuid()), given.Backer(Guid.NewGuid()));
        var a = await Place(anna, campaign, 6_000, campaign.TierIds[0]);
        var b = await Place(ben, campaign, 4_900, campaign.TierIds[0]);
        var c = await Place(cleo, campaign, 10_000);

        Assert.Equal(HttpStatusCode.OK, (await Change(anna, a, 10_000, campaign.TierIds[1])).StatusCode);   // up, and to another tier
        Assert.Equal(HttpStatusCode.OK, (await Change(cleo, c, 2_500)).StatusCode);                         // down
        Assert.Equal(HttpStatusCode.OK, (await ben.DeleteAsync(b, Ct)).StatusCode);                          // out
        Assert.Equal(HttpStatusCode.OK, (await Change(cleo, c, 4_900, campaign.TierIds[0])).StatusCode);   // into the place Anna and Ben freed

        await AssertTotalIsTheLedger(given, campaign.CampaignId);
        Assert.Equal(14_900L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1, await given.Scalar<int>("SELECT claimed FROM pledges.tier_counters WHERE campaign_id = @id AND limit_qty = 2", campaign.CampaignId));
        Assert.Equal(7L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId)); // 3 activations, 3 changes, 1 cancel
    }

    [Fact]
    public async Task Nothing_changes_after_the_deadline()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 6_000);
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 millisecond' WHERE campaign_id = @id", campaign.CampaignId);

        var changed = await Change(backer, pledge, 9_000);
        var cancelled = await backer.DeleteAsync(pledge, Ct);

        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("campaign_closed", await Reason(changed));
        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        Assert.Equal("campaign_closed", await Reason(cancelled));
        Assert.Equal("ACTIVE", (await backer.GetFromJsonAsync<JsonElement>(pledge, Ct)).GetProperty("status").GetString());
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task A_move_to_a_sold_out_tier_changes_nothing()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 1), ("Standard copy", 5_900, null));
        await Place(given.Backer(Guid.NewGuid()), campaign, 4_900, campaign.TierIds[0]);
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 6_000, campaign.TierIds[1]);

        var refused = await Change(backer, pledge, 7_000, campaign.TierIds[0]);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("tier_sold_out", await Reason(refused));
        Assert.Equal(6_000, (await backer.GetFromJsonAsync<JsonElement>(pledge, Ct)).GetProperty("amount_minor").GetInt64());
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task After_a_cancel_the_backer_may_pledge_again()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var first = await Place(backer, campaign, 6_000);
        (await backer.DeleteAsync(first, Ct)).EnsureSuccessStatusCode();

        await Place(backer, campaign, 7_000);

        Assert.Equal("CANCELLED", (await backer.GetFromJsonAsync<JsonElement>(first, Ct)).GetProperty("status").GetString());
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task A_cancelled_pledge_cannot_be_changed_or_cancelled_again()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 6_000);
        (await backer.DeleteAsync(pledge, Ct)).EnsureSuccessStatusCode();

        var again = await backer.DeleteAsync(pledge, Ct);
        var changed = await Change(backer, pledge, 9_000);

        Assert.Equal("pledge_not_active", await Reason(again));
        Assert.Equal("pledge_not_active", await Reason(changed));
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task Concurrent_changes_of_one_pledge_keep_the_total_exact()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 6_000);

        await Task.WhenAll(Enumerable.Range(1, 10).Select(i => Change(backer, pledge, 6_000 + (i * 100))));

        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }
}
