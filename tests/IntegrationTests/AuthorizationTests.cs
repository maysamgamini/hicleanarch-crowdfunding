using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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

        var campaignId = Guid.ParseExact(id!["cmp_".Length..], "N");

        Assert.Equal(creator, await new Scenario(database, _host).Scalar<Guid>("SELECT creator_id FROM campaigns.campaigns WHERE id = @id", campaignId));
    }

    /// <summary>A draft is not public before it is published: anyone but its creator is told there is no such campaign.</summary>
    [Fact]
    public async Task A_draft_is_shown_only_to_its_creator()
    {
        var creator = Guid.NewGuid();
        var created = await CreateDraft(Database.Token(creator, Roles.Creator));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("campaign_id").GetString();

        var own = await Database.Client(_host, Database.Token(creator, Roles.Creator)).GetAsync($"/v1/campaigns/{id}", TestContext.Current.CancellationToken);
        var anonymous = await _host.CreateClient().GetAsync($"/v1/campaigns/{id}", TestContext.Current.CancellationToken);
        var anotherCreator = await Database.Client(_host, Database.Token(Roles.Creator)).GetAsync($"/v1/campaigns/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, anotherCreator.StatusCode);
    }

    [Fact]
    public async Task A_published_campaign_is_public_and_does_not_name_its_creators_user_id()
    {
        var campaign = await new Scenario(database, _host).Publish();

        var read = await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", TestContext.Current.CancellationToken);

        Assert.Equal("LIVE", read.GetProperty("status").GetString());
        Assert.False(read.TryGetProperty("creator_id", out _));
    }

    /// <summary>The endpoints anyone may call, signed in or not. Every other endpoint names its requirement.</summary>
    private static readonly string[] Public =
    [
        "* /health",
        "* /health/live",
        "* /health/ready",
        "GET /openapi/{documentName}.json",
        "GET /v1/campaigns/{id}",
        "GET /v1/campaigns/{id}/totals",
        "POST /v1/webhooks/psp",
    ];

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task Every_endpoint_names_its_requirement_and_only_the_public_ones_allow_anyone(string environment)
    {
        await using var host = database.StartHost().WithWebHostBuilder(web => web
            .UseEnvironment(environment)
            .UseSetting("ConnectionStrings:Valkey", ""));
        var expected = environment == "Development"
            ? [.. Public, "POST /dev/fake-psp/setups/{setupId}/complete"]
            : Public;

        var anonymous = new List<string>();
        foreach (var (name, endpoint) in MappedEndpoints.Of(host, payments: true))
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                anonymous.Add(name);
                continue;
            }

            Assert.True(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0, $"{name} says neither RequireAuthorization nor AllowAnonymous.");
        }

        Assert.Equal(expected.Order(StringComparer.Ordinal), anonymous.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_endpoint_that_names_no_policy_still_needs_a_signed_in_user()
    {
        var fallback = await _host.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }
}
