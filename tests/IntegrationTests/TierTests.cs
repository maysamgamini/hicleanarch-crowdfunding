using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Pledges.Application;
using Pledges.Infrastructure.Persistence;

namespace IntegrationTests;

/// <summary>Lesson 6 under load: a limited tier never sells more than its limit.</summary>
public sealed class TierTests(Database database) : IDisposable
{
    // These tests hold a tier row open on purpose to watch another transaction wait for it; under the
    // whole suite's load that can take longer than the 300 ms a pledge waits in production.
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host =
        database.StartHost().WithWebHostBuilder(web => web.UseSetting("Pledges:TierWait", "00:00:10"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// One backer's pledge, sent again with the same key while the API says it is busy (409 with
    /// Retry-After: tier_busy, or try_again once the database ends a transaction at the module role's
    /// 2 s limit), as a client does, until it gets a real answer: its status and reason. Fifty pledges
    /// queue on one row here, so on a loaded machine some are told to try again.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string? Reason)> PledgeUntilAnswered(Scenario given, Scenario.Published campaign)
    {
        using var response = await Scenario.SendUntilAnswered(given.Backer(Guid.NewGuid()), () =>
            new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/pledges")
            {
                Content = JsonContent.Create(Scenario.Pledge(4_900, campaign.TierIds[0])),
            });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return (response.StatusCode, body.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null);
    }

    [Fact]
    public async Task Fifty_parallel_pledges_for_ten_places_give_exactly_ten()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => PledgeUntilAnswered(given, campaign)));

        var answers = string.Join(", ", responses.GroupBy(r => $"{(int)r.Status} {r.Reason}").Select(g => $"{g.Key} x{g.Count()}"));
        Assert.True(responses.Count(r => r.Status == HttpStatusCode.Created) == 10, answers);
        Assert.True(responses.Count(r => r.Status == HttpStatusCode.Conflict && r.Reason == "tier_sold_out") == 40, answers);

        Assert.Equal(10, await given.Scalar<int>("SELECT claimed FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(49_000L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
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

    /// <summary>
    /// The port on its own, with two open transactions, as a second pledge would meet it once the
    /// funding window's lock no longer lines one campaign's pledges up (Round 2). The condition in
    /// the update, not the order of requests, is what refuses the second claim.
    /// </summary>
    [Fact]
    public async Task Two_claims_for_the_last_place_give_one_place()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 1));
        var tier = Guid.ParseExact(campaign.TierIds[0]["tir_".Length..], "N");

        await using var scope1 = _host.Services.CreateAsyncScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var t1 = await db1.Database.BeginTransactionAsync(Ct);
        Assert.True(await scope1.ServiceProvider.GetRequiredService<ITierSlots>().TryClaim(tier, Ct));

        await using var scope2 = _host.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var t2 = await db2.Database.BeginTransactionAsync(Ct);
        var second = scope2.ServiceProvider.GetRequiredService<ITierSlots>().TryClaim(tier, Ct);
        await Task.Delay(100, Ct);
        Assert.False(second.IsCompleted); // it waits for the row lock

        await t1.CommitAsync(Ct);
        Assert.False(await second); // then re-checks the row as the first left it: no room
        await t2.CommitAsync(Ct);

        Assert.Equal(1, await given.Scalar<int>("SELECT claimed FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
    }

    /// <summary>
    /// Opposite moves started together, many times over. Each takes its tier rows in tier id order,
    /// so the two can only queue on the same first row; locked in request order, a pair would sooner
    /// or later each hold one row and wait for the other's, and PostgreSQL would end one as a deadlock.
    /// </summary>
    [Fact]
    public async Task Opposite_moves_started_together_never_deadlock()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 100), ("Founder", 9_900, 100));
        var early = Guid.ParseExact(campaign.TierIds[0]["tir_".Length..], "N");
        var founder = Guid.ParseExact(campaign.TierIds[1]["tir_".Length..], "N");
        await given.Execute("UPDATE pledges.tier_counters SET claimed = 50 WHERE campaign_id = @id", campaign.CampaignId);

        for (var round = 0; round < 20; round++)
        {
            using var start = new Barrier(2);
            async Task Move(Guid from, Guid to)
            {
                await using var scope = _host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PledgesDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                start.SignalAndWait(Ct);
                Assert.True(await scope.ServiceProvider.GetRequiredService<ITierSlots>().TryMove(from, to, Ct));
                await transaction.CommitAsync(Ct);
            }

            await Task.WhenAll(Task.Run(() => Move(early, founder), Ct), Task.Run(() => Move(founder, early), Ct));
        }

        Assert.Equal(100L, await given.Scalar<long>("SELECT sum(claimed)::bigint FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
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
        await Task.Delay(100, Ct);
        Assert.False(opposite.IsCompleted); // it waits for the first, on the same row first, instead of deadlocking

        await t1.CommitAsync(Ct);
        Assert.True(await opposite);
        await t2.CommitAsync(Ct);

        Assert.Equal(2L, await given.Scalar<long>("SELECT sum(claimed)::bigint FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
    }
}
