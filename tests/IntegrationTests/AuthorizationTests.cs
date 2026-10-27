using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

namespace IntegrationTests;

/// <summary>Who may do what: the token's groups, its client and its kind decide.</summary>
public sealed class AuthorizationTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static readonly object Draft = new
    {
        title = "Harbor Lights",
        goal_minor = 5_000_000,
        currency = "USD",
        deadline = "2027-04-01T03:59:00Z",
        tiers = Array.Empty<object>(),
    };

    public void Dispose() => _host.Dispose();

    private Task<HttpResponseMessage> CreateDraft(string? token)
    {
        return Database.Client(_host, token).PostAsJsonAsync("/v1/campaigns", Draft, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_token_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateDraft(null)).StatusCode);

    [Fact]
    public async Task A_backer_cannot_create_a_campaign() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateDraft(Database.Token(Roles.Backer))).StatusCode);

    [Fact]
    public async Task A_token_for_another_app_client_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await CreateDraft(LocalTokens.Issue(Database.SigningKey, "someone-else", Guid.NewGuid(), Roles.Creator))).StatusCode);

    [Fact]
    public async Task A_token_signed_with_another_key_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await CreateDraft(LocalTokens.Issue("another-key-that-is-long-enough-0123456789", Database.ClientId, Guid.NewGuid(), Roles.Creator))).StatusCode);

    [Fact]
    public async Task An_expired_token_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await CreateDraft(LocalTokens.Issue(Database.SigningKey, Database.ClientId, Guid.NewGuid(), "access",
                DateTime.UtcNow.AddMinutes(-10), Roles.Creator))).StatusCode);

    [Fact]
    public async Task An_id_token_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await CreateDraft(LocalTokens.Issue(Database.SigningKey, Database.ClientId, Guid.NewGuid(), "id",
                DateTime.UtcNow.AddHours(1), Roles.Creator))).StatusCode);

    [Fact]
    public async Task The_draft_records_the_creator_from_the_token()
    {
        var creator = Guid.NewGuid();
        var created = await CreateDraft(Database.Token(creator, Roles.Creator));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("campaign_id").GetString();

        var read = await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(creator, read.GetProperty("creator_id").GetGuid());
    }
}
