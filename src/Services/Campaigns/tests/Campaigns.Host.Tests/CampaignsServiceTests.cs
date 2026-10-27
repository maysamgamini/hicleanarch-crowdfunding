using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

using Npgsql;

namespace Campaigns.Host.Tests;

/// <summary>
/// Campaigns' endpoints, served by the Campaigns service with the same sign-in as in the monolith: a
/// creator's draft, its publish, which goes to the broker for Pledges and the others, and its read.
/// </summary>
public sealed class CampaignsServiceTests(TestHost service)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Draft => new
    {
        title = "Harbor Lights",
        goal_minor = 5_000_000,
        currency = "USD",
        deadline = DateTimeOffset.UtcNow.AddDays(30),
        tiers = new[] { new { title = "Early Bird", min_pledge_minor = 4_900, limit = (int?)null } },
    };

    private static HttpRequestMessage Post(string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public async Task Only_a_creator_drafts_and_the_published_campaign_goes_to_the_broker()
    {
        await using var host = service.Start();
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post("/v1/campaigns", null, Draft), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/v1/campaigns", TestHost.Token(Roles.Backer), Draft), Ct)).StatusCode);

        var creator = TestHost.Token(Roles.Creator);
        var created = await client.SendAsync(Post("/v1/campaigns", creator, Draft), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("campaign_id").GetString()!;
        (await client.SendAsync(Post($"/v1/campaigns/{id}/publish", creator), Ct)).EnsureSuccessStatusCode();

        var read = await client.GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", Ct);
        Assert.Equal("LIVE", read.GetProperty("status").GetString());

        // CampaignPublished is owed to the broker (Pledges, Payments and Notifications run elsewhere) and published.
        await using var connection = new NpgsqlConnection(service.Owner);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM campaigns.outbox WHERE type = 'CampaignPublished' AND 'broker' = ANY (sinks) AND published_at IS NOT NULL", connection);
        var until = DateTime.UtcNow.AddSeconds(30);
        long published;
        while ((published = (long)(await command.ExecuteScalarAsync(Ct))!) == 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(200, Ct);
        }

        Assert.Equal(1, published);
    }
}
