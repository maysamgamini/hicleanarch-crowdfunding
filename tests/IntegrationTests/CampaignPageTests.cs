using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l06: the campaign page puts the catalog (Campaigns) and the totals (Pledges) together
/// itself, reading the totals from the endpoint the CDN caches. The campaign reply's totals fields
/// are deprecated and still filled, until they are removed before Campaigns runs alone.
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

    /// <summary>Campaigns' in-process way to Pledges' totals, answering fixed numbers: the page must not need it.</summary>
    private sealed class FixedTotals(long pledgedMinor, int backers) : ICampaignTotals
    {
        public Task<CampaignTotals?> Get(Guid campaignId, CancellationToken cancellationToken) => Task.FromResult<CampaignTotals?>(new CampaignTotals(pledgedMinor, backers));
    }

    [Fact]
    public async Task The_page_shows_the_totals_endpoints_numbers_and_needs_no_call_from_Campaigns_to_Pledges()
    {
        await using var host = database.StartHost(s => s.AddScoped<ICampaignTotals>(_ => new FixedTotals(0, 0)));
        var given = new Scenario(database, host);
        var campaign = await given.Publish();
        (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        var client = host.CreateClient();

        await Scenario.Eventually(async () => (await CampaignPage.Load(client, campaign.Id)).PledgedMinor == 6_000);
        var page = await CampaignPage.Load(client, campaign.Id);
        var reply = await client.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct);

        Assert.Equal(("Harbor Lights", "LIVE", 6_000L, 1), (page.Title, page.Status, page.PledgedMinor, page.Backers));
        // The CDN keeps the page's totals 2 seconds (s3-l05), never past the deadline (below).
        Assert.Equal("public, max-age=2", page.TotalsCacheControl);
        // The reply's own totals came from Campaigns' call into Pledges, which here answers nothing.
        Assert.Equal(0, reply.GetProperty("pledged_minor").GetInt64());
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
        var page = await CampaignPage.Load(late.CreateClient(), campaign.Id);

        Assert.Equal("LIVE", page.Status);
        Assert.Equal(cacheControl, page.TotalsCacheControl);
    }

    [Fact]
    public async Task The_reply_still_fills_its_totals_fields_and_says_they_are_deprecated()
    {
        await using var host = database.StartHost(s => s.AddScoped<ICampaignTotals>(_ => new FixedTotals(1_234, 7)));
        var given = new Scenario(database, host);
        var campaign = await given.Publish();

        var response = await host.CreateClient().GetAsync($"/v1/campaigns/{campaign.Id}", Ct);
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        // Filled in process, from Campaigns' call into Pledges, as before.
        Assert.Equal((1_234L, 7), (reply.GetProperty("pledged_minor").GetInt64(), reply.GetProperty("backers").GetInt32()));
        Assert.Equal("@" + new Campaigns.Infrastructure.ReplyTotalsOptions().DeprecatedAt.ToUnixTimeSeconds(), Assert.Single(response.Headers.GetValues("Deprecation")));
        Assert.Equal("Mon, 01 Feb 2027 00:00:00 GMT", Assert.Single(response.Headers.GetValues("Sunset")));
        Assert.Contains($"</v1/campaigns/{campaign.Id}/totals>; rel=\"successor-version\"", response.Headers.GetValues("Link"));
    }

    [Fact]
    public async Task The_OpenAPI_document_marks_the_two_fields_deprecated_and_nothing_else()
    {
        await using var host = database.StartHost();

        var document = await host.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);

        var properties = document.GetProperty("components").GetProperty("schemas").GetProperty("CampaignView").GetProperty("properties");
        var deprecated = properties.EnumerateObject()
            .Where(p => p.Value.TryGetProperty("deprecated", out var d) && d.GetBoolean())
            .Select(p => p.Name);
        Assert.Equal(["backers", "pledged_minor"], deprecated.Order(StringComparer.Ordinal));
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

    // The scripts read the totals endpoint only through as_of, and the reply's totals by name never
    // (".backers" alone is the tokens file's list of backers, not a field of a reply).
    [GeneratedRegex("""(\.json\(\s*["'](pledged_minor|backers)["']\s*\))|(\.pledged_minor\b)""")]
    private static partial Regex ReadsReplyTotals();
}
