using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Pledges.Application;
using Pledges.Infrastructure.Persistence;

namespace IntegrationTests;

/// <summary>Placing a pledge: each rule of lessons 4, 5 and 7 against a real database.</summary>
public sealed class PledgeTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private static async Task<string> Reason(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString()!;

    [Fact]
    public async Task A_backer_pledges_and_the_total_moves()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());

        var placed = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000, campaign.TierIds[0]), Ct);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var body = await placed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.Equal($"/v1/pledges/{body.GetProperty("pledge_id").GetString()}", placed.Headers.Location?.ToString());
        // The page's total is the aggregator's, a moment behind the ledger (lesson s3-l05).
        await Scenario.Eventually(async () =>
        {
            var read = await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct);
            return read.GetProperty("pledged_minor").GetInt64() == 6_000 && read.GetProperty("backers").GetInt32() == 1;
        });
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT sum(delta_minor)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task The_same_key_twice_gives_the_same_pledge_once()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var key = Guid.NewGuid().ToString();
        HttpRequestMessage Pledge()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/pledges") { Content = JsonContent.Create(Scenario.Pledge(6_000)) };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }

        var first = await backer.SendAsync(Pledge(), Ct);
        var second = await backer.SendAsync(Pledge(), Ct);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_second_live_pledge_from_the_same_backer_is_409_already_backing()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);

        var second = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("already_backing", await Reason(second));
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_pledge_to_a_cancelled_campaign_before_its_deadline_is_409_campaign_closed()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        (await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("CANCELLED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.True(await given.Scalar<bool>("SELECT deadline > now() FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));

        var refused = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("campaign_closed", await Reason(refused));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_pledge_after_the_deadline_is_409_campaign_closed()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 millisecond' WHERE campaign_id = @id", campaign.CampaignId);

        var refused = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("campaign_closed", await Reason(refused));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Theory]
    [InlineData(4_000, true, "below_tier_minimum")]
    [InlineData(0, false, "amount_not_positive")]
    public async Task A_bad_amount_is_422_and_nothing_counts(long amount, bool withTier, string reason)
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();

        var refused = await given.Backer(Guid.NewGuid())
            .PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(amount, withTier ? campaign.TierIds[0] : null), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(reason, await Reason(refused));
        Assert.Equal(0L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task Only_a_backer_may_pledge()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();

        var refused = await given.Creator().PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_backer_reads_their_own_pledge_and_nobody_elses()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backerId = Guid.NewGuid();
        var placed = await given.Backer(backerId).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);
        var location = placed.Headers.Location!.ToString();

        var mine = await given.Backer(backerId).GetFromJsonAsync<JsonElement>(location, Ct);
        var someoneElse = await given.Backer(Guid.NewGuid()).GetAsync(location, Ct);

        Assert.Equal("ACTIVE", mine.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, someoneElse.StatusCode);
    }

    [Fact]
    public async Task The_module_role_can_add_to_the_ledger_but_not_change_it()
    {
        var ct = Ct;
        await using var pledges = new NpgsqlConnection(database.Pledges);
        await pledges.OpenAsync(ct);

        // Adding is allowed; the test rolls its own entry back.
        await using (var transaction = await pledges.BeginTransactionAsync(ct))
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind)
                VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 'test:{Guid.NewGuid():N}', 100, 'ACTIVATE')
                """, pledges, transaction);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
            await transaction.RollbackAsync(ct);
        }

        foreach (var sql in new[] { "UPDATE pledges.pledge_ledger SET delta_minor = 1", "DELETE FROM pledges.pledge_ledger", "TRUNCATE pledges.pledge_ledger" })
        {
            await using var change = new NpgsqlCommand(sql, pledges);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => change.ExecuteNonQueryAsync(ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
    }
}
