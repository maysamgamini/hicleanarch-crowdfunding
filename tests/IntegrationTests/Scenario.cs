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

    /// <summary>
    /// The deadline every Scenario campaign has: 30 days from today, midnight UTC, inside the 60 days
    /// a campaign may run from its publishing.
    /// </summary>
    public static readonly DateTimeOffset Deadline = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(30);

    public Guid CreatorId { get; } = Guid.NewGuid();

    public string OwnerConnection => database.Owner;

    public HttpClient Creator() => Database.Client(host, Database.Token(CreatorId, Roles.Creator));

    public HttpClient Backer(Guid backerId) => Database.Client(host, Database.Token(backerId, Roles.Backer));

    /// <summary>A published campaign: its public id, and the public ids of its tiers in order.</summary>
    public sealed record Published(string Id, Guid CampaignId, IReadOnlyList<string> TierIds);

    private static readonly (string Title, long MinPledgeMinor, int? Limit)[] DefaultTiers = [("Early Bird", 4_900, 500), ("Standard copy", 5_900, null)];

    public async Task<string> Draft(long goalMinor = 5_000_000, params (string Title, long MinPledgeMinor, int? Limit)[] tiers)
    {
        if (tiers.Length == 0)
        {
            tiers = DefaultTiers;
        }

        var created = await Creator().PostAsJsonAsync("/v1/campaigns", new
        {
            title = "Harbor Lights",
            goal_minor = goalMinor,
            currency = "USD",
            deadline = Deadline,
            tiers = tiers.Select(t => new { title = t.Title, min_pledge_minor = t.MinPledgeMinor, limit = t.Limit }).ToArray(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("campaign_id").GetString()!;
    }

    /// <summary>
    /// Creates and publishes a campaign, and waits until Pledges has opened its funding window. The
    /// tier ids come back in the order the tiers were given.
    /// </summary>
    public async Task<Published> Publish(long goalMinor = 5_000_000, params (string Title, long MinPledgeMinor, int? Limit)[] tiers)
    {
        if (tiers.Length == 0)
        {
            tiers = DefaultTiers;
        }

        var id = await Draft(goalMinor, tiers);
        (await Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct)).EnsureSuccessStatusCode();
        var campaignId = Guid.ParseExact(id["cmp_".Length..], "N");
        await Eventually(async () => await Scalar<long>("SELECT count(*) FROM pledges.funding_windows WHERE campaign_id = @id", campaignId) == 1);
        var read = await host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", Ct);
        var byTitle = read.GetProperty("tiers").EnumerateArray()
            .ToDictionary(t => t.GetProperty("title").GetString()!, t => t.GetProperty("tier_id").GetString()!);
        return new Published(id, campaignId, [.. tiers.Select(t => byTitle[t.Title])]);
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

    /// <summary>A pledge request with a saved card; tier is a public tier id or null.</summary>
    public static object Pledge(long amountMinor, string? tierId = null) =>
        new { amount_minor = amountMinor, tier_id = tierId, payment_method_id = "pm_card_visa" };

    /// <summary>
    /// Sends a request, and sends it again with the same Idempotency-Key for as long as the API answers
    /// 409 with Retry-After (busy: nothing was kept, try again), after waiting as long as it says, as a
    /// client does. Returns the first other answer.
    /// </summary>
    public static async Task<HttpResponseMessage> SendUntilAnswered(HttpClient client, Func<HttpRequestMessage> request)
    {
        var key = Guid.NewGuid().ToString();
        for (var attempt = 1; ; attempt++)
        {
            using var message = request();
            message.Headers.Add("Idempotency-Key", key);
            var response = await client.SendAsync(message, Ct);
            // A busy answer (409) or a database that did not answer in time (503), each with Retry-After,
            // asks the client to send the same request again with its key; anything else is the answer.
            if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable)
                || response.Headers.RetryAfter?.Delta is not { } wait)
            {
                return response;
            }

            Assert.True(attempt < 30, $"Still told to try again after {attempt} tries: {await response.Content.ReadAsStringAsync(Ct)}");
            response.Dispose();
            await Task.Delay(wait, Ct);
        }
    }

    /// <summary>
    /// Waits for something an event or a background job does. It returns as soon as the condition
    /// holds, so the bound only matters on a loaded machine, where deliveries queue up: it is long
    /// enough not to fail a test that is merely slow there.
    /// </summary>
    public static async Task Eventually(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < until && !await condition())
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(await condition());
    }
}
