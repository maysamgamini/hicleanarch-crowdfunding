using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Identity;

/// <summary>The two kinds of user. In Cognito they are groups, carried in the token's <c>cognito:groups</c> claim.</summary>
public static class Roles
{
    public const string Creator = "creator";
    public const string Backer = "backer";
}

/// <summary>Authorization policies, by the role they require.</summary>
public static class Policies
{
    public const string Creator = "creator";
    public const string Backer = "backer";
}

/// <summary>
/// Checks every request's bearer token without calling the identity provider. With
/// <c>Auth:Authority</c> set (Cognito's issuer URL) the signing keys come from the provider's
/// published key set; without it, from <c>Auth:LocalSigningKey</c>, for development and tests.
/// Either way the token must be an access token for this app client.
/// </summary>
public static class Authentication
{
    public static IServiceCollection AddTokenAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection("Auth");
        var clientId = auth["ClientId"] ?? throw new InvalidOperationException("Auth:ClientId is not set.");
        var authority = auth["Authority"];

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            var validation = jwt.TokenValidationParameters;
            if (!string.IsNullOrEmpty(authority))
            {
                jwt.Authority = authority;
                validation.ValidIssuer = authority;
                validation.ValidAlgorithms = ["RS256"];
            }
            else
            {
                var key = auth["LocalSigningKey"] ?? throw new InvalidOperationException("Set Auth:Authority, or Auth:LocalSigningKey for local tokens.");
                validation.ValidIssuer = LocalTokens.Issuer;
                validation.IssuerSigningKey = LocalTokens.SigningKey(key);
                validation.ValidAlgorithms = ["HS256"];
            }

            // Cognito's access tokens name the app client in client_id and carry no aud claim.
            validation.ValidateAudience = false;
            validation.NameClaimType = "sub";
            validation.RoleClaimType = "cognito:groups";
            jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var token = context.Principal!;
                    if (token.FindFirstValue("token_use") != "access" || token.FindFirstValue("client_id") != clientId)
                    {
                        context.Fail("Not an access token for this app client.");
                    }

                    return Task.CompletedTask;
                },
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Creator, policy => policy.RequireRole(Roles.Creator))
            .AddPolicy(Policies.Backer, policy => policy.RequireRole(Roles.Backer));
        return services;
    }

    /// <summary>The caller's user id: the token's <c>sub</c>, a UUID in Cognito.</summary>
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("sub") ?? throw new InvalidOperationException("The token has no sub claim."));
}
