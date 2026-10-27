using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>The public totals reply, its cache headers, and the flip at the deadline.</summary>
public sealed class TotalsTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The deadline every Scenario campaign has.</summary>
    private static readonly DateTimeOffset Deadline = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> ReadAt(string id, DateTimeOffset? now = null)
    {
        await using var host = database.StartHost(services =>
        {
            if (now is { } at)
            {
                services.AddSingleton<TimeProvider>(new FixedClock(at));
            }
        });
        var response = await host.CreateClient().GetAsync($"/v1/campaigns/{id}/totals", Ct);
        var body = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadFromJsonAsync<JsonElement>(Ct)
            : default;
        return (response, body);
    }

    private async Task<Scenario.Published> Publish()
    {
        await using var host = database.StartHost();
        return await new Scenario(database, host).Publish();
    }

    [Fact]
    public async Task A_live_campaign_is_labelled_not_exact_and_cached_for_two_seconds()
    {
        var published = await Publish();

        var (response, body) = await ReadAt(published.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=2", response.Headers.CacheControl?.ToString());
        Assert.Equal("LIVE", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("pledging_open").GetBoolean());
        Assert.False(body.GetProperty("exact").GetBoolean());
        Assert.Equal(JsonValueKind.String, body.GetProperty("as_of").ValueKind);
        Assert.Equal(0, body.GetProperty("pledged_minor").GetInt64());
        var tiers = body.GetProperty("tiers").EnumerateArray().ToDictionary(t => t.GetProperty("tier_id").GetString()!);
        Assert.Equal(500, tiers[published.TierIds[0]].GetProperty("remaining").GetInt32());
        Assert.Equal(JsonValueKind.Null, tiers[published.TierIds[1]].GetProperty("remaining").ValueKind);
    }

    [Fact]
    public async Task At_the_deadline_the_reply_says_ended_before_the_close_job_has_run()
    {
        var published = await Publish();

        var (response, body) = await ReadAt(published.Id, Deadline);

        Assert.Equal("ENDED", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("pledging_open").GetBoolean());
        Assert.Equal("public, max-age=2", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(1500, "public, max-age=1")]
    [InlineData(500, "no-store")]
    public async Task A_reply_cached_before_the_deadline_never_outlives_it(int millisecondsLeft, string cacheControl)
    {
        var published = await Publish();

        var (response, body) = await ReadAt(published.Id, Deadline.AddMilliseconds(-millisecondsLeft));

        Assert.Equal("LIVE", body.GetProperty("status").GetString());
        Assert.Equal(cacheControl, response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task After_the_decision_the_total_is_the_exact_sum()
    {
        var published = await Publish();
        await using (var host = database.StartHost())
        {
            await new Scenario(database, host).Execute(
                "UPDATE pledges.funding_windows SET status = 'FUNDED', final_minor = 5120000, ended_at = now() WHERE campaign_id = @id",
                published.CampaignId);
        }

        var (_, body) = await ReadAt(published.Id, Deadline.AddMinutes(1));

        Assert.True(body.GetProperty("exact").GetBoolean());
        Assert.Equal(5_120_000, body.GetProperty("pledged_minor").GetInt64());
        Assert.Equal("FUNDED", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_unknown_campaign_is_404_and_not_cached()
    {
        var (response, _) = await ReadAt($"cmp_{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
