using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Pledges.Application;
using Pledges.Infrastructure.Persistence;

namespace IntegrationTests;

/// <summary>Lesson 6 under load: a limited tier never sells more than its limit.</summary>
public sealed class TierTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Fifty_parallel_pledges_for_ten_places_give_exactly_ten()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct)));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(40, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Equal("tier_sold_out", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        }

        Assert.Equal(10, await given.Scalar<int>("SELECT claimed FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(49_000L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(10L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task The_check_constraint_refuses_an_oversell_from_any_code_path()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));

        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            given.Execute("UPDATE pledges.tier_counters SET claimed = 11 WHERE campaign_id = @id", campaign.CampaignId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        Assert.Equal("never_oversell", refused.ConstraintName);
    }

    [Fact]
    public async Task A_tier_with_no_limit_is_never_counted()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Standard copy", 5_900, null));

        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(5_900, campaign.TierIds[0]), Ct);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        Assert.Equal(0, await given.Scalar<int>("SELECT claimed FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task Two_moves_in_opposite_directions_both_finish()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10), ("Founder", 9_900, 10));
        var early = Guid.ParseExact(campaign.TierIds[0]["tir_".Length..], "N");
        var founder = Guid.ParseExact(campaign.TierIds[1]["tir_".Length..], "N");
        await given.Execute("UPDATE pledges.tier_counters SET claimed = 1 WHERE campaign_id = @id", campaign.CampaignId);

        await using var scope1 = _host.Services.CreateAsyncScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var t1 = await db1.Database.BeginTransactionAsync(Ct);
        Assert.True(await scope1.ServiceProvider.GetRequiredService<ITierSlots>().TryMove(early, founder, Ct));

        await using var scope2 = _host.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var t2 = await db2.Database.BeginTransactionAsync(Ct);
        var opposite = scope2.ServiceProvider.GetRequiredService<ITierSlots>().TryMove(founder, early, Ct);
        await Task.Delay(200, Ct);
        Assert.False(opposite.IsCompleted); // it waits for the first, on the same row first, instead of deadlocking

        await t1.CommitAsync(Ct);
        Assert.True(await opposite);
        await t2.CommitAsync(Ct);

        Assert.Equal(2L, await given.Scalar<long>("SELECT sum(claimed)::bigint FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
    }
}
