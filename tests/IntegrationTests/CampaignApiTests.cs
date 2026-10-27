using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

namespace IntegrationTests;

/// <summary>The API, run in memory through the real host against a real database: requests in, JSON out.</summary>
public sealed class CampaignApiTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private HttpClient Client
    {
        get
        {
            var client = _host.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", Database.Token(Roles.Creator));
            return client;
        }
    }

    public void Dispose() => _host.Dispose();

    private static object Draft(string title = "Harbor Lights", long goalMinor = 5_000_000) => new
    {
        title,
        goal_minor = goalMinor,
        currency = "USD",
        deadline = "2027-03-31T23:59:00-04:00",
        tiers = new object[]
        {
            new { title = "Early Bird", min_pledge_minor = 4_900, limit = 500 },
            new { title = "Standard copy", min_pledge_minor = 5_900, limit = (int?)null },
        },
    };

    [Fact]
    public async Task A_created_draft_can_be_read_back()
    {
        var created = await Client.PostAsJsonAsync("/v1/campaigns", Draft(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = body.GetProperty("campaign_id").GetString();
        Assert.StartsWith("cmp_", id, StringComparison.Ordinal);
        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        Assert.Equal($"/v1/campaigns/{id}", created.Headers.Location?.ToString());

        var read = await Client.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(id, read.GetProperty("campaign_id").GetString());
        Assert.Equal("DRAFT", read.GetProperty("status").GetString());
        Assert.Equal(5_000_000, read.GetProperty("goal_minor").GetInt64());
        Assert.Equal("2027-04-01T03:59:00Z", read.GetProperty("deadline").GetString());
        Assert.Equal(2, read.GetProperty("tiers").GetArrayLength());
    }

    [Fact]
    public async Task A_campaign_that_does_not_exist_is_404()
    {
        var unknown = await Client.GetAsync($"/v1/campaigns/cmp_{Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var malformed = await Client.GetAsync("/v1/campaigns/not-an-id", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Theory]
    [InlineData("", 5_000_000, "bad_title")]
    [InlineData("Harbor Lights", 0, "goal_not_positive")]
    public async Task A_bad_draft_is_422_with_a_reason(string title, long goalMinor, string reason)
    {
        var response = await Client.PostAsJsonAsync("/v1/campaigns", Draft(title, goalMinor), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(reason, problem.GetProperty("reason").GetString());
    }
}
