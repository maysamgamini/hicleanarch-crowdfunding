using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>Publishing, and the idempotency key every change carries.</summary>
public sealed class PublishTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();
    private readonly Guid _creator = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private HttpClient Creator() => Database.Client(_host, Database.Token(_creator, Roles.Creator));

    private static object Draft(string title = "Harbor Lights", DateTimeOffset? deadline = null) => new
    {
        title,
        goal_minor = 5_000_000,
        currency = "USD",
        deadline = deadline ?? Scenario.Deadline,
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
        var id = await CreateDraft(Draft(deadline: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var refused = await Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task A_deadline_more_than_60_days_away_is_a_draft_but_is_not_published()
    {
        var id = await CreateDraft(Draft(deadline: DateTimeOffset.UtcNow.AddDays(61)));

        var refused = await Creator().PostAsync($"/v1/campaigns/{id}/publish", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("deadline_too_far", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        var read = await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", Ct);
        Assert.Equal("DRAFT", read.GetProperty("status").GetString());
    }

    /// <summary>
    /// Two publishes of one draft, each in its own open transaction, both past the domain's check: the
    /// database's DRAFT condition lets one win. The second waits for the first's row lock, then finds
    /// the campaign LIVE and moves nothing.
    /// </summary>
    [Fact]
    public async Task Two_publishes_at_once_give_one_live_campaign_and_one_event()
    {
        var id = await CreateDraft();
        var campaignId = Campaigns.Domain.CampaignId.TryParse(id, out var parsed) ? parsed : throw new InvalidOperationException(id);
        await using var first = _host.Services.CreateAsyncScope();
        await using var second = _host.Services.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<Campaigns.Infrastructure.Persistence.CampaignsDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<Campaigns.Infrastructure.Persistence.CampaignsDbContext>();
        await using var firstTransaction = await firstDb.Database.BeginTransactionAsync(Ct);
        await using var secondTransaction = await secondDb.Database.BeginTransactionAsync(Ct);
        Task<BuildingBlocks.Domain.Result<Campaigns.Domain.CampaignStatus>> Publish(AsyncServiceScope scope) =>
            scope.ServiceProvider.GetRequiredService<BuildingBlocks.Application.IDispatcher>()
                .Send<Campaigns.Application.PublishCampaign, Campaigns.Domain.CampaignStatus>(new Campaigns.Application.PublishCampaign(campaignId, _creator), Ct);

        var won = await Publish(first); // holds the row's lock until its transaction ends
        var firstSession = await firstDb.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(Ct);
        var racing = Publish(second); // reads DRAFT, passes the domain check, waits for the lock
        await Scenario.Eventually(async () => await BlockedBy(firstSession) == 1);
        await firstTransaction.CommitAsync(Ct);
        var lost = await racing;
        await secondTransaction.CommitAsync(Ct);

        Assert.True(won.IsSuccess);
        Assert.Equal("not_a_draft", Assert.Single(lost.Errors).Code);
        Assert.Equal(1L, await database.Count("campaigns.outbox", "payload->>'campaignId'", campaignId.Value.ToString()));
    }

    /// <summary>Sessions waiting for a lock the given session holds, as PostgreSQL reports them.</summary>
    private async Task<long> BlockedBy(int session)
    {
        await using var owner = new Npgsql.NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE @session = ANY(pg_blocking_pids(pid))", owner);
        read.Parameters.AddWithValue("session", session);
        return (long)(await read.ExecuteScalarAsync(Ct))!;
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
        var title = $"Once {key}";
        var first = await Creator().SendAsync(Post("/v1/campaigns", Draft(title), key), Ct);
        var second = await Creator().SendAsync(Post("/v1/campaigns", Draft(title), key), Ct);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, await database.Count("campaigns.campaigns", "title", title));
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
    public async Task Five_requests_sent_together_with_one_key_create_one_campaign()
    {
        // Whether they overlap is up to the scheduler; whichever way they interleave, one campaign results.
        var key = Guid.NewGuid().ToString();
        var title = $"Together {key}";
        var replies = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Creator().SendAsync(Post("/v1/campaigns", Draft(title), key), Ct)));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(replies.Select(async r =>
            (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("campaign_id").GetString()));
        Assert.Single(ids.Distinct());
        Assert.Equal(4, replies.Count(r => r.Headers.Contains("Idempotent-Replayed")));
        Assert.Equal(1, await database.Count("campaigns.campaigns", "title", title));
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

    [Fact]
    public async Task Keys_older_than_seven_days_are_purged()
    {
        var oldKey = Guid.NewGuid().ToString();
        var newKey = Guid.NewGuid().ToString();
        await Creator().SendAsync(Post("/v1/campaigns", Draft($"Old {oldKey}"), oldKey), Ct);
        await Creator().SendAsync(Post("/v1/campaigns", Draft($"New {newKey}"), newKey), Ct);
        await database.Execute($"UPDATE campaigns.idempotency_keys SET created_at = now() - interval '8 days' WHERE key = '{oldKey}'");

        foreach (var purge in _host.Services.GetServices<BuildingBlocks.Infrastructure.Persistence.IIdempotencyKeyPurge>())
        {
            await purge.PurgeOnce(Ct);
        }

        Assert.Equal(0, await database.Count("campaigns.idempotency_keys", "key", oldKey));
        Assert.Equal(1, await database.Count("campaigns.idempotency_keys", "key", newKey));
    }

    [Theory]
    [InlineData("campaigns")]
    [InlineData("pledges")]
    [InlineData("payments")]
    public async Task Every_module_purges_its_keys_older_than_seven_days(string schema)
    {
        var oldKey = Guid.NewGuid().ToString();
        var newKey = Guid.NewGuid().ToString();
        await database.Execute($"""
            INSERT INTO {schema}.idempotency_keys (caller_id, key, request_hash, reply_status, created_at)
            VALUES (gen_random_uuid(), '{oldKey}', '\x00', 200, now() - interval '8 days'),
                   (gen_random_uuid(), '{newKey}', '\x00', 200, now())
            """);

        foreach (var purge in _host.Services.GetServices<BuildingBlocks.Infrastructure.Persistence.IIdempotencyKeyPurge>())
        {
            await purge.PurgeOnce(Ct);
        }

        Assert.Equal(0, await database.Count($"{schema}.idempotency_keys", "key", oldKey));
        Assert.Equal(1, await database.Count($"{schema}.idempotency_keys", "key", newKey));
    }
}
