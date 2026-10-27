using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Infrastructure.Identity;

/// <summary>
/// Who this service is when it calls another: a Cognito app client allowed only the client-credentials
/// grant (section <c>ServiceIdentity</c>). Without <c>TokenEndpoint</c> it signs its own tokens with
/// <c>Auth:LocalSigningKey</c>, for development and tests; a receiver that trusts Cognito refuses those.
/// </summary>
public sealed class ServiceIdentityOptions
{
    public string ClientId { get; set; } = "";

    /// <summary>From Secrets Manager, through the task's execution role; never in an image or a file.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>The user pool domain's <c>/oauth2/token</c>.</summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>How long before its expiry a token is replaced: Cognito bills every token it issues.</summary>
    public TimeSpan RefreshBefore { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>One token request; it runs inside the first call's attempt, so it must leave that attempt time.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When the receiver refuses a token fetched less than this long ago, the cause is configuration (the
    /// client is not listed, the issuer or scope is wrong), not a stale token: the refused token is kept
    /// this long, and no new one is asked for meanwhile, so the token requests Cognito bills stay bounded.
    /// </summary>
    public TimeSpan RefusedTokenBackoff { get; set; } = TimeSpan.FromMinutes(1);
}

public static class ServiceIdentity
{
    /// <summary>The HTTP client that asks the token endpoint.</summary>
    public const string TokenClient = "service-identity-tokens";

    public static IServiceCollection AddServiceIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceIdentityOptions>().Bind(configuration.GetSection("ServiceIdentity"))
            .Validate(o => o.ClientId.Length > 0, "ServiceIdentity:ClientId is not set.")
            .Validate(o => o.TokenEndpoint is null || o.ClientSecret.Length > 0, "ServiceIdentity:ClientSecret is not set.")
            .Validate(o => o.TokenEndpoint is not null || configuration["Auth:LocalSigningKey"] is { Length: > 0 },
                "Set ServiceIdentity:TokenEndpoint, or Auth:LocalSigningKey for local tokens.")
            .Validate(o => o.RefreshBefore > TimeSpan.Zero && o.RequestTimeout > TimeSpan.Zero && o.RefusedTokenBackoff > TimeSpan.Zero,
                "ServiceIdentity: RefreshBefore, RequestTimeout and RefusedTokenBackoff must be positive.")
            .ValidateOnStart();
        services.AddHttpClient(TokenClient, (sp, http) => http.Timeout = sp.GetRequiredService<IOptions<ServiceIdentityOptions>>().Value.RequestTimeout);
        services.TryAddSingleton(sp => new ServiceTokenSource(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<ServiceIdentityOptions>>().Value,
            configuration["Auth:LocalSigningKey"],
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILogger<ServiceTokenSource>>() ?? NullLogger<ServiceTokenSource>.Instance));
        return services;
    }

    /// <summary>Every request this client sends carries a service token for the scope.</summary>
    public static IHttpClientBuilder AddServiceToken(this IHttpClientBuilder client, string scope) =>
        client.AddHttpMessageHandler(sp => new ServiceTokenHandler(sp.GetRequiredService<ServiceTokenSource>(), scope));
}

/// <summary>
/// Hands out a token per scope and keeps it until <see cref="ServiceIdentityOptions.RefreshBefore"/>
/// before it expires: with Cognito's one-hour tokens, one request per scope every 55 minutes, however
/// many calls. Callers that arrive together while there is none wait for one request.
/// </summary>
public sealed partial class ServiceTokenSource(
    IHttpClientFactory http, ServiceIdentityOptions options, string? localSigningKey, TimeProvider clock, ILogger<ServiceTokenSource> logger) : IDisposable
{
    private static readonly TimeSpan LocalLifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _asking = new(1, 1);

    public async ValueTask<string> Get(string scope, CancellationToken cancellationToken)
    {
        if (_held.TryGetValue(scope, out var held) && clock.GetUtcNow() < held.RefreshAt)
        {
            return held.Token;
        }

        await _asking.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            if (_held.TryGetValue(scope, out held) && now < held.RefreshAt)
            {
                return held.Token;
            }

            try
            {
                var (token, lifetime) = await Request(scope, now, cancellationToken);
                // A token shorter than twice the margin is kept for half its life instead.
                var margin = lifetime > options.RefreshBefore * 2 ? options.RefreshBefore : lifetime / 2;
                var fresh = new Held(token, now + lifetime - margin, now + lifetime, now);
                _held[scope] = fresh;
                Fetched(scope, fresh.RefreshAt);
                return token;
            }
            catch (HttpRequestException e) when (held is not null && now < held.ExpiresAt)
            {
                // The token endpoint is down while the old token is still good: keep using it until it expires.
                RefreshFailed(e, scope, held.ExpiresAt);
                return held.Token;
            }
        }
        finally
        {
            _asking.Release();
        }
    }

    public void Dispose() => _asking.Dispose();

    /// <summary>
    /// The receiver refused this token. A token held for a while is stale (a key rotated, a client
    /// changed): it is dropped, and the next call asks for a new one. A token refused within
    /// <see cref="ServiceIdentityOptions.RefusedTokenBackoff"/> of its fetch would be refused again: it
    /// is kept until that time has passed, and calls meanwhile fail without asking the endpoint, so a
    /// refusal that persists costs one token request per back-off, not one per call.
    /// </summary>
    public void Forget(string scope, string token)
    {
        if (!_held.TryGetValue(scope, out var held) || held.Token != token || held.Refused)
        {
            return;
        }

        var now = clock.GetUtcNow();
        if (now - held.FetchedAt >= options.RefusedTokenBackoff)
        {
            _held.TryRemove(new KeyValuePair<string, Held>(scope, held));
        }
        else if (_held.TryUpdate(scope, held with { RefreshAt = now + options.RefusedTokenBackoff, Refused = true }, held))
        {
            FreshTokenRefused(scope, now + options.RefusedTokenBackoff);
        }
    }

    private async Task<(string Token, TimeSpan Lifetime)> Request(string scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (options.TokenEndpoint is null)
        {
            return (LocalTokens.IssueService(localSigningKey!, options.ClientId, (now + LocalLifetime).UtcDateTime, scope), LocalLifetime);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", scope)]),
        };
        // RFC 6749 section 2.3.1: the client id and secret, form-encoded, as HTTP Basic credentials.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(options.ClientId)}:{Uri.EscapeDataString(options.ClientSecret)}")));

        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(ServiceIdentity.TokenClient).SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The token endpoint did not answer in time.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"The token endpoint answered {(int)response.StatusCode}.", null, response.StatusCode);
            }

            var reply = await response.Content.ReadFromJsonAsync<TokenReply>(cancellationToken);
            if (reply is not { AccessToken.Length: > 0, ExpiresIn: > 0 })
            {
                throw new HttpRequestException("The token endpoint's reply has no access token.", null, HttpStatusCode.BadGateway);
            }

            return (reply.AccessToken, TimeSpan.FromSeconds(reply.ExpiresIn));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Service token for {Scope} fetched; it is replaced at {RefreshAt}")]
    private partial void Fetched(string scope, DateTimeOffset refreshAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service token for {Scope} not renewed; the current one is used until {ExpiresAt}")]
    private partial void RefreshFailed(Exception e, string scope, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A fresh service token for {Scope} was refused: the receiver does not accept this client, issuer or scope. No new token is asked for before {RetryAt}")]
    private partial void FreshTokenRefused(string scope, DateTimeOffset retryAt);

    private sealed record Held(string Token, DateTimeOffset RefreshAt, DateTimeOffset ExpiresAt, DateTimeOffset FetchedAt, bool Refused = false);

    private sealed record TokenReply(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>
/// Puts the service's token on each request; a 401 back makes the source drop the token, or keep a fresh
/// one it would only get again for a while (<see cref="ServiceTokenSource.Forget"/>).
/// </summary>
public sealed class ServiceTokenHandler(ServiceTokenSource tokens, string scope) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.Get(scope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            tokens.Forget(scope, token);
        }

        return response;
    }
}
