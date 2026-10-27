using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l06: the campaign page puts the catalog (Campaigns) and the totals (Pledges) together
/// itself, reading the totals from the endpoint the CDN caches. The campaign reply's totals fields,
/// deprecated at s5-l06, are removed at s5-l08, before Campaigns runs alone.
/// </summary>
public sealed partial class CampaignPageTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What a campaign page shows, loaded as the page loads it: two reads, no call between services.</summary>
    private sealed record CampaignPage(string Title, string Status, long PledgedMinor, int Backers, string TotalsCacheControl)
    {
        public static async Task<CampaignPage> Load(HttpClient client, string campaignId)
        {
            var catalog = await client.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaignId}", Ct);
            using var totalsResponse = await client.GetAsync($"/v1/campaigns/{campaignId}/totals", Ct);
            var totals = await totalsResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
            return new CampaignPage(
                catalog.GetProperty("title").GetString()!,
                catalog.GetProperty("status").GetString()!,
                totals.GetProperty("pledged_minor").GetInt64(),
                totals.GetProperty("backers").GetInt32(),
                totalsResponse.Headers.CacheControl?.ToString() ?? "");
        }
    }

    /// <summary>Campaigns' old in-process way to Pledges' totals, answering fixed numbers: the page must not need it.</summary>
    private sealed class FixedTotals(long pledgedMinor, int backers) : ICampaignTotals
    {
        public Task<CampaignTotals?> Get(Guid campaignId, CancellationToken cancellationToken) => Task.FromResult<CampaignTotals?>(new CampaignTotals(pledgedMinor, backers));
    }

    /// <summary>The same way in, counting its calls: since s5-l08 nothing in Campaigns makes any.</summary>
    private sealed class CountingTotals(Action called) : ICampaignTotals
    {
        public Task<CampaignTotals?> Get(Guid campaignId, CancellationToken cancellationToken)
        {
            called();
            return Task.FromResult<CampaignTotals?>(null);
        }
    }

    [Fact]
    public async Task The_page_shows_the_totals_endpoints_numbers_and_needs_no_call_from_Campaigns_to_Pledges()
    {
        await using var host = database.StartHost(s => s.AddScoped<ICampaignTotals>(_ => new FixedTotals(0, 0)));
        var given = new Scenario(database, host);
        var campaign = await given.Publish();
        (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        var client = Database.Client(host);

        await Scenario.Eventually(async () => (await CampaignPage.Load(client, campaign.Id)).PledgedMinor == 6_000);
        var page = await CampaignPage.Load(client, campaign.Id);
        var reply = await client.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct);

        Assert.Equal(("Harbor Lights", "LIVE", 6_000L, 1), (page.Title, page.Status, page.PledgedMinor, page.Backers));
        // The CDN keeps the page's totals 2 seconds (s3-l05), never past the deadline (below).
        Assert.Equal("public, max-age=2", page.TotalsCacheControl);
        // The reply carries no totals of its own (s5-l08).
        Assert.False(reply.TryGetProperty("pledged_minor", out _));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory]
    [InlineData(10_000, "public, max-age=2")]
    [InlineData(1_500, "public, max-age=1")]
    [InlineData(500, "no-store")]
    public async Task The_pages_totals_are_cached_two_seconds_and_never_past_the_deadline(int millisecondsLeft, string cacheControl)
    {
        Scenario.Published campaign;
        await using (var host = database.StartHost())
        {
            campaign = await new Scenario(database, host).Publish();
        }

        await using var late = database.StartHost(s => s.AddSingleton<TimeProvider>(new FixedClock(Scenario.Deadline.AddMilliseconds(-millisecondsLeft))));
        var page = await CampaignPage.Load(Database.Client(late), campaign.Id);

        Assert.Equal("LIVE", page.Status);
        Assert.Equal(cacheControl, page.TotalsCacheControl);
    }

    [Fact]
    public async Task The_reply_has_no_totals_and_Campaigns_asks_Pledges_nothing()
    {
        // s5-l08's contract step: the deprecated fields are gone, and so is Campaigns' call for them.
        var asked = 0;
        await using var host = database.StartHost(s => s.AddScoped<ICampaignTotals>(_ => new CountingTotals(() => Interlocked.Increment(ref asked))));
        var given = new Scenario(database, host);
        var campaign = await given.Publish();

        var response = await Database.Client(host).GetAsync($"/v1/campaigns/{campaign.Id}", Ct);
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.False(reply.TryGetProperty("pledged_minor", out _));
        Assert.False(reply.TryGetProperty("backers", out _));
        Assert.False(response.Headers.Contains("Deprecation"));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void No_load_script_reads_the_totals_from_the_campaign_reply()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CrowdFunding.slnx")))
        {
            root = root.Parent;
        }

        var scripts = Directory.GetFiles(Path.Combine(root!.FullName, "tests", "load"), "*.js");

        Assert.NotEmpty(scripts);
        Assert.All(scripts, script => Assert.DoesNotMatch(ReadsReplyTotals(), File.ReadAllText(script)));
    }

    // The scripts read the totals by name only from the totals endpoint's reply, held in a variable
    // named for it (launchTotals): never from the campaign reply (".backers" alone is the tokens file's
    // list of backers, not a field of a reply).
    [GeneratedRegex("""(\.json\(\s*["'](pledged_minor|backers)["']\s*\))|((?<!Totals)\.pledged_minor\b)""")]
    private static partial Regex ReadsReplyTotals();
}
