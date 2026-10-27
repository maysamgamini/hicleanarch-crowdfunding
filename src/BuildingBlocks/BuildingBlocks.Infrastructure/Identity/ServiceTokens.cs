using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Identity;

/// <summary>
/// The second way in: tokens a service gets for itself with Cognito's client-credentials grant. A
/// scheme of its own beside the users' one, with the same issuer and keys. It accepts an access token
/// only from an app client listed in <c>Auth:ServiceClients</c>, and its policies ask for scopes, never
/// groups. The users' scheme stays the default, and its client check already refuses these tokens.
/// </summary>
public static class ServiceTokens
{
    public const string Scheme = "ServiceToken";

    /// <summary>Adds the scheme once, however many modules receive calls from other services.</summary>
    public static IServiceCollection AddServiceTokenAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(s => s.ServiceType == typeof(Marker)))
        {
            return services;
        }

        var auth = configuration.GetSection("Auth");
        // None listed: no service is let in, so an internal endpoint is closed until a caller is named.
        var allowed = auth.GetSection("ServiceClients").Get<string[]>() ?? [];
        if (allowed.Contains(auth["ClientId"]))
        {
            throw new InvalidOperationException("Auth:ServiceClients must not name the users' app client (Auth:ClientId).");
        }

        services.AddSingleton<Marker>();
        services.AddAuthentication().AddJwtBearer(Scheme, jwt =>
        {
            Authentication.TrustIssuer(jwt, auth);
            jwt.TokenValidationParameters.NameClaimType = "client_id";
            jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    // A user's access token always names the user (username); a client-credentials token never does.
                    var token = context.Principal!;
                    if (token.FindFirstValue("token_use") != "access" || !allowed.Contains(token.FindFirstValue("client_id"))
                        || token.HasClaim(c => c.Type == "username"))
                    {
                        context.Fail("Not an access token of an allowed service.");
                    }

                    return Task.CompletedTask;
                },
            };
        });
        return services;
    }

    /// <summary>
    /// A policy named after its scope: a valid service token whose <c>scope</c> claim lists it.
    /// No token, or a user's, is a 401; a service token without the scope is a 403.
    /// </summary>
    public static AuthorizationBuilder AddScopePolicy(this AuthorizationBuilder authorization, string scope) =>
        authorization.AddPolicy(scope, policy => policy
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.HasScope(scope)));

    /// <summary>Cognito puts the granted scopes in one claim, separated by spaces.</summary>
    public static bool HasScope(this ClaimsPrincipal caller, string scope) =>
        caller.FindAll("scope").Any(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal));

    private sealed class Marker;
}
