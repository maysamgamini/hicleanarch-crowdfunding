using System.Net;
using System.Net.Http.Json;
using System.Text;

using BuildingBlocks.Infrastructure.Identity;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Payments.Infrastructure;

namespace IntegrationTests;

/// <summary>
/// Who may call Payments' internal endpoints (lesson s4-l04): only a listed service, with a token for
/// the scope. The receiver here is a small host with Payments' own caller rules and the users'
/// scheme, the way the monolith composes them; the internal endpoint stands in for the card-setup one.
/// </summary>
public sealed class ServiceTokenTests(Database database)
{
    private const string Monolith = "test-monolith";
    private const string InternalPath = "/internal/payments/card-setups";

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ServiceToken(string clientId, params string[] scopes) =>
        LocalTokens.IssueService(Database.SigningKey, clientId, DateTime.UtcNow.AddHours(1), scopes);

    private static async Task<WebApplication> StartReceiver(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:ClientId"] = Database.ClientId,
            ["Auth:LocalSigningKey"] = Database.SigningKey,
            ["Auth:ServiceClients:0"] = Monolith,
        });
        builder.Configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.Services.AddTokenAuthentication(builder.Configuration);
        builder.Services.AddPaymentsCallers(builder.Configuration);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost(InternalPath, (HttpContext http) => Results.Ok(new { caller = http.User.Identity!.Name })).RequirePaymentsCommands();
        app.MapGet("/backer-only", () => Results.Ok()).RequireAuthorization(Policies.Backer);
        await app.StartAsync(Ct);
        return app;
    }

    private static async Task<HttpStatusCode> CallInternal(WebApplication receiver, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, InternalPath);
        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        using var response = await receiver.GetTestClient().SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task No_token_is_401()
    {
        await using var receiver = await StartReceiver();
        Assert.Equal(HttpStatusCode.Unauthorized, await CallInternal(receiver, null));
    }

    [Fact]
    public async Task The_monoliths_token_with_the_commands_scope_is_let_in()
    {
        await using var receiver = await StartReceiver();
        Assert.Equal(HttpStatusCode.OK, await CallInternal(receiver, ServiceToken(Monolith, "payments/commands")));
    }

    [Fact]
    public async Task A_token_without_the_scope_is_403()
    {
        await using var receiver = await StartReceiver();
        Assert.Equal(HttpStatusCode.Forbidden, await CallInternal(receiver, ServiceToken(Monolith, "payments/reads")));
        Assert.Equal(HttpStatusCode.Forbidden, await CallInternal(receiver, ServiceToken(Monolith)));
    }

    [Fact]
    public async Task A_service_that_is_not_listed_is_401_even_with_the_scope()
    {
        await using var receiver = await StartReceiver();
        Assert.Equal(HttpStatusCode.Unauthorized, await CallInternal(receiver, ServiceToken("another-service", "payments/commands")));
    }

    [Fact]
    public async Task A_user_token_is_refused_on_the_internal_endpoint()
    {
        await using var receiver = await StartReceiver();
        Assert.Equal(HttpStatusCode.Unauthorized, await CallInternal(receiver, Database.Token(Roles.Backer)));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallInternal(receiver, Database.Token(Roles.Creator)));
    }

    [Fact]
    public async Task A_user_signed_in_through_the_service_client_is_401_even_with_the_scope()
    {
        await using var receiver = await StartReceiver();
        var userThroughServiceClient = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalTokens.Issuer,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(LocalTokens.SigningKey(Database.SigningKey), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.NewGuid().ToString(),
                ["username"] = "backer@example.test",
                ["client_id"] = Monolith,
                ["token_use"] = "access",
                ["scope"] = "payments/commands",
            },
        });

        Assert.Equal(HttpStatusCode.Unauthorized, await CallInternal(receiver, userThroughServiceClient));
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_401()
    {
        await using var receiver = await StartReceiver();
        Assert.Equal(HttpStatusCode.Unauthorized, await CallInternal(receiver,
            LocalTokens.IssueService("another-key-that-is-long-enough-0123456789", Monolith, DateTime.UtcNow.AddHours(1), "payments/commands")));
    }

    [Fact]
    public async Task The_users_app_client_cannot_be_listed_as_a_service()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartReceiver(("Auth:ServiceClients:1", Database.ClientId)));
        Assert.Contains("Auth:ServiceClients", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_composes_the_service_scheme_beside_the_users_default_one()
    {
        await using var host = database.StartHost();

        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal("Bearer", (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);
        Assert.NotNull(await schemes.GetSchemeAsync(ServiceTokens.Scheme));
        var policy = await host.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync("payments/commands");
        Assert.Equal(new[] { ServiceTokens.Scheme }, policy!.AuthenticationSchemes);
    }

    [Theory]
    [InlineData("POST", "/v1/campaigns")]
    [InlineData("POST", "/v1/campaigns/cmp_0000000000000000000000/pledges")]
    [InlineData("GET", "/v1/pledges/plg_0000000000000000000000")]
    [InlineData("PUT", "/v1/pledges/plg_0000000000000000000000/payment-method")]
    [InlineData("GET", "/v1/campaigns/cmp_0000000000000000000000/settlement")]
    public async Task A_service_token_is_refused_on_backer_and_creator_endpoints(string method, string path)
    {
        await using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("Auth:ServiceClients:0", Monolith));
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new("Bearer", ServiceToken(Monolith, "payments/commands"));

        using var response = await Database.Client(host).SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Payments_internal_card_setup_endpoint_lets_in_only_the_monoliths_token_with_the_commands_scope()
    {
        await using var host = database.StartHost();
        var client = host.CreateClient();

        async Task<HttpStatusCode> Call(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Payments.Contracts.PaymentsInternalEndpoints.StartCardSetup)
            {
                Content = JsonContent.Create(new { pledge_id = Guid.NewGuid(), backer_id = Guid.NewGuid() }),
            };
            if (token is not null)
            {
                request.Headers.Authorization = new("Bearer", token);
            }

            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Call(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(Database.Token(Roles.Backer)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(ServiceToken("another-service", "payments/commands")));
        Assert.Equal(HttpStatusCode.Forbidden, await Call(ServiceToken(Database.ServiceClientId)));
        Assert.Equal(HttpStatusCode.OK, await Call(ServiceToken(Database.ServiceClientId, "payments/commands")));
    }

    // The caller's side: a token per expiry window, whatever the number of calls.

    [Fact]
    public async Task One_token_request_per_expiry_window()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var cognito = new FakeTokenEndpoint();
        using var services = Caller(clock, cognito);
        var tokens = services.GetRequiredService<ServiceTokenSource>();

        var first = await tokens.Get("payments/commands", Ct);
        for (var minute = 1; minute < 55; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(first, await tokens.Get("payments/commands", Ct));
        }

        Assert.Equal(1, cognito.Requests);

        clock.Advance(TimeSpan.FromMinutes(1)); // 55 minutes: five before the hour's token expires
        var second = await tokens.Get("payments/commands", Ct);

        Assert.NotEqual(first, second);
        Assert.Equal(2, cognito.Requests);
    }

    [Fact]
    public async Task Callers_that_arrive_together_share_one_token_request()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var cognito = new FakeTokenEndpoint() { Delay = TimeSpan.FromMilliseconds(100) };
        using var services = Caller(clock, cognito);
        var tokens = services.GetRequiredService<ServiceTokenSource>();

        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => tokens.Get("payments/commands", Ct).AsTask()));

        Assert.Single(all.Distinct());
        Assert.Equal(1, cognito.Requests);
    }

    [Fact]
    public async Task The_request_is_a_client_credentials_grant_for_the_scope()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var cognito = new FakeTokenEndpoint();
        using var services = Caller(clock, cognito);

        await services.GetRequiredService<ServiceTokenSource>().Get("payments/commands", Ct);

        Assert.Equal("Basic", cognito.LastAuthorization!.Scheme);
        Assert.Equal($"{Monolith}:test-secret", Encoding.UTF8.GetString(Convert.FromBase64String(cognito.LastAuthorization.Parameter!)));
        Assert.Equal("grant_type=client_credentials&scope=payments%2Fcommands", cognito.LastBody);
    }

    [Fact]
    public async Task With_the_token_endpoint_down_the_held_token_is_used_until_it_expires()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var cognito = new FakeTokenEndpoint();
        using var services = Caller(clock, cognito);
        var tokens = services.GetRequiredService<ServiceTokenSource>();
        var held = await tokens.Get("payments/commands", Ct);

        cognito.Down = true;
        clock.Advance(TimeSpan.FromMinutes(58));
        Assert.Equal(held, await tokens.Get("payments/commands", Ct));

        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsAsync<HttpRequestException>(() => tokens.Get("payments/commands", Ct).AsTask());
    }

    [Fact]
    public async Task A_caller_sends_its_token_and_reuses_it()
    {
        await using var receiver = await StartReceiver();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var cognito = new FakeTokenEndpoint();
        using var services = Caller(clock, cognito, receiver);
        var payments = services.GetRequiredService<IHttpClientFactory>().CreateClient("payments");

        for (var i = 0; i < 3; i++)
        {
            using var response = await payments.PostAsync(InternalPath, null, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Monolith, (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!["caller"]);
        }

        Assert.Equal(1, cognito.Requests);
    }

    [Fact]
    public async Task A_refused_token_is_dropped_and_the_next_call_asks_for_a_new_one()
    {
        await using var receiver = await StartReceiver();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var cognito = new FakeTokenEndpoint() { IssueFor = "a-client-payments-no-longer-lists" };
        using var services = Caller(clock, cognito, receiver);
        var payments = services.GetRequiredService<IHttpClientFactory>().CreateClient("payments");

        using (var refused = await payments.PostAsync(InternalPath, null, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        cognito.IssueFor = Monolith;
        using var accepted = await payments.PostAsync(InternalPath, null, Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, cognito.Requests);
    }

    [Fact]
    public async Task Without_a_token_endpoint_the_caller_signs_local_tokens_the_receiver_accepts()
    {
        await using var receiver = await StartReceiver();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var services = Caller(clock, cognito: null, receiver);

        using var response = await services.GetRequiredService<IHttpClientFactory>().CreateClient("payments").PostAsync(InternalPath, null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The monolith's side: its service identity, and a client to Payments that carries the token.</summary>
    private static ServiceProvider Caller(ManualClock clock, FakeTokenEndpoint? cognito, WebApplication? receiver = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:LocalSigningKey"] = Database.SigningKey,
            ["ServiceIdentity:ClientId"] = Monolith,
            ["ServiceIdentity:ClientSecret"] = "test-secret",
            ["ServiceIdentity:TokenEndpoint"] = cognito is null ? null : "https://auth.example.test/oauth2/token",
        };
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddServiceIdentity(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        if (cognito is not null)
        {
            services.AddHttpClient(ServiceIdentity.TokenClient).ConfigurePrimaryHttpMessageHandler(() => cognito);
        }

        if (receiver is not null)
        {
            services.AddHttpClient("payments", http => http.BaseAddress = receiver.GetTestServer().BaseAddress)
                .ConfigurePrimaryHttpMessageHandler(() => receiver.GetTestServer().CreateHandler())
                .AddServiceToken("payments/commands");
        }

        return services.BuildServiceProvider();
    }

    /// <summary>Cognito's token endpoint: one-hour tokens signed with the test key, counted.</summary>
    private sealed class FakeTokenEndpoint : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        public string IssueFor { get; set; } = Monolith;

        public bool Down { get; set; }

        public TimeSpan Delay { get; init; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(Delay, cancellationToken);
            if (Down)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            LastAuthorization = request.Headers.Authorization;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var scope = LastBody.Split('&').Single(p => p.StartsWith("scope=", StringComparison.Ordinal))["scope=".Length..];
            // The token's own expiry is real time: the receiver checks it against its own clock.
            var token = LocalTokens.IssueService(Database.SigningKey, IssueFor, DateTime.UtcNow.Add(Hour), Uri.UnescapeDataString(scope));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { access_token = token, expires_in = 3600, token_type = "Bearer" }),
            };
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
