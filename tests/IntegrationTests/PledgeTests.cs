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
        var read = await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct);
        Assert.Equal(6_000, read.GetProperty("pledged_minor").GetInt64());
        Assert.Equal(1, read.GetProperty("backers").GetInt32());
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
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
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
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
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
        Assert.Equal(0L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
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
        await using var update = new NpgsqlCommand("UPDATE pledges.pledge_ledger SET delta_minor = 1", pledges);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_close_waits_for_a_pledge_in_flight_and_a_late_pledge_is_refused()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() + interval '1500 milliseconds' WHERE campaign_id = @id", campaign.CampaignId);

        // Pledge A updates the window before the deadline, and keeps its row lock open.
        await using var scopeA = _host.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var a = await dbA.Database.BeginTransactionAsync(Ct);
        Assert.True(await scopeA.ServiceProvider.GetRequiredService<IFundingWindows>().TryAccept(campaign.CampaignId, 6_000, 1, Ct));

        // Pledge B's transaction also starts before the deadline, so its now() is before it too.
        await using var scopeB = _host.Services.CreateAsyncScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var b = await dbB.Database.BeginTransactionAsync(Ct);
        await dbB.Database.ExecuteSqlRawAsync("SELECT 1", Ct);

        await Task.Delay(1_600, Ct);

        // The close, once the deadline has passed: it must wait for pledge A.
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var close = new NpgsqlCommand(
            "UPDATE pledges.funding_windows SET status = 'ENDED', ended_at = now() WHERE campaign_id = @id AND status = 'LIVE' AND deadline <= now()", owner);
        close.Parameters.AddWithValue("id", campaign.CampaignId);
        var closing = close.ExecuteNonQueryAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.False(closing.IsCompleted);

        // Pledge B reaches the row now, behind the close.
        var late = scopeB.ServiceProvider.GetRequiredService<IFundingWindows>().TryAccept(campaign.CampaignId, 6_000, 1, Ct);
        await Task.Delay(300, Ct);
        Assert.False(late.IsCompleted);

        await a.CommitAsync(Ct);

        Assert.Equal(1, await closing);
        Assert.False(await late);
        await b.RollbackAsync(Ct);
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal("ENDED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }
}
