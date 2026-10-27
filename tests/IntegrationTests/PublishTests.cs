using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

namespace IntegrationTests;

/// <summary>Publishing, and the idempotency key every change carries.</summary>
public sealed class PublishTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();
    private readonly Guid _creator = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private HttpClient Creator() => Database.Client(_host, Database.Token(_creator, Roles.Creator));

    private static object Draft(string title = "Harbor Lights", string deadline = "2030-01-01T00:00:00Z") => new
    {
        title,
        goal_minor = 5_000_000,
        currency = "USD",
        deadline,
        tiers = new[] { new { title = "Early Bird", min_pledge_minor = 4_900, limit = 500 } },
    };

    private async Task<string> CreateDraft(object? draft = null)
    {
        var created = await Creator().PostAsJsonAsync("/v1/campaigns", draft ?? Draft(), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("campaign_id").GetString()!;
    }

    private static HttpRequestMessage Post(string path, object? body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task A_draft_is_published_and_reads_as_live()
    {
        var id = await CreateDraft();

        var published = await Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct);

        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var read = await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", Ct);
        Assert.Equal("LIVE", read.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Publishing_twice_with_new_keys_is_409_the_second_time()
    {
        var id = await CreateDraft();
        await Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct);

        var again = await Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("not_a_draft", (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_campaign_whose_deadline_has_passed_cannot_be_published()
    {
        var id = await CreateDraft(Draft(deadline: "2020-01-01T00:00:00Z"));

        var refused = await Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task Only_its_creator_may_publish_a_campaign()
    {
        var id = await CreateDraft();
        var someoneElse = Database.Client(_host, Database.Token(Roles.Creator));

        var refused = await someoneElse.PostAsync($"/v1/campaigns/{id}/publish", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_change_without_an_idempotency_key_is_400()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Database.Token(Roles.Creator));

        var refused = await client.PostAsJsonAsync("/v1/campaigns", Draft(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task The_same_key_twice_gets_the_same_reply_and_one_campaign()
    {
        var key = Guid.NewGuid().ToString();
        var first = await Creator().SendAsync(Post("/v1/campaigns", Draft(), key), Ct);
        var second = await Creator().SendAsync(Post("/v1/campaigns", Draft(), key), Ct);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_422()
    {
        var key = Guid.NewGuid().ToString();
        await Creator().SendAsync(Post("/v1/campaigns", Draft("One"), key), Ct);

        var reused = await Creator().SendAsync(Post("/v1/campaigns", Draft("Another"), key), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", (await reused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_same_key_sent_at_once_from_two_connections_creates_one_campaign()
    {
        var key = Guid.NewGuid().ToString();
        var replies = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Creator().SendAsync(Post("/v1/campaigns", Draft(), key), Ct)));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(replies.Select(async r =>
            (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("campaign_id").GetString()));
        Assert.Single(ids.Distinct());
        Assert.Equal(4, replies.Count(r => r.Headers.Contains("Idempotent-Replayed")));
    }

    [Fact]
    public async Task A_refused_request_stores_nothing_so_its_retry_is_checked_again()
    {
        var key = Guid.NewGuid().ToString();
        var refused = await Creator().SendAsync(Post("/v1/campaigns", new { title = "", goal_minor = 1, currency = "USD", deadline = "2030-01-01T00:00:00Z" }, key), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var retried = await Creator().SendAsync(Post("/v1/campaigns", new { title = "", goal_minor = 1, currency = "USD", deadline = "2030-01-01T00:00:00Z" }, key), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, retried.StatusCode);
        Assert.False(retried.Headers.Contains("Idempotent-Replayed"));
    }
}
