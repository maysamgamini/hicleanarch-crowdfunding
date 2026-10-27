using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

using Microsoft.AspNetCore.Mvc.Testing;

using Npgsql;

namespace IntegrationTests;

/// <summary>The steps most tests start from: a creator, a published campaign, its funding window.</summary>
public sealed class Scenario(Database database, WebApplicationFactory<Program> host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Guid CreatorId { get; } = Guid.NewGuid();

    public HttpClient Creator() => Database.Client(host, Database.Token(CreatorId, Roles.Creator));

    public HttpClient Backer(Guid backerId) => Database.Client(host, Database.Token(backerId, Roles.Backer));

    /// <summary>A published campaign: its public id, and the public ids of its tiers in order.</summary>
    public sealed record Published(string Id, Guid CampaignId, IReadOnlyList<string> TierIds);

    public async Task<string> Draft(long goalMinor = 5_000_000, params (string Title, long MinPledgeMinor, int? Limit)[] tiers)
    {
        if (tiers.Length == 0)
        {
            tiers = [("Early Bird", 4_900, 500), ("Standard copy", 5_900, null)];
        }

        var created = await Creator().PostAsJsonAsync("/v1/campaigns", new
        {
            title = "Harbor Lights",
            goal_minor = goalMinor,
            currency = "USD",
            deadline = "2030-01-01T00:00:00Z",
            tiers = tiers.Select(t => new { title = t.Title, min_pledge_minor = t.MinPledgeMinor, limit = t.Limit }).ToArray(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("campaign_id").GetString()!;
    }

    /// <summary>Creates and publishes a campaign, and waits until Pledges has opened its funding window.</summary>
    public async Task<Published> Publish(long goalMinor = 5_000_000, params (string Title, long MinPledgeMinor, int? Limit)[] tiers)
    {
        var id = await Draft(goalMinor, tiers);
        (await Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct)).EnsureSuccessStatusCode();
        var campaignId = Guid.ParseExact(id["cmp_".Length..], "N");
        await Eventually(async () => await Scalar<long>("SELECT count(*) FROM pledges.funding_windows WHERE campaign_id = @id", campaignId) == 1);
        var read = await host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", Ct);
        return new Published(id, campaignId, [.. read.GetProperty("tiers").EnumerateArray().Select(t => t.GetProperty("tier_id").GetString()!)]);
    }

    /// <summary>One value from the database, read as the owner.</summary>
    public async Task<T> Scalar<T>(string sql, Guid id)
    {
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new NpgsqlCommand(sql, owner);
        read.Parameters.AddWithValue("id", id);
        return (T)(await read.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Runs SQL as the owner, for what a test sets up behind the API's back.</summary>
    public async Task Execute(string sql, Guid id)
    {
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, owner);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(await condition());
    }
}
