using System.Text;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BuildingBlocks.Infrastructure.Identity;

/// <summary>
/// Tokens shaped like Cognito's access tokens, signed with a local key, for development and tests.
/// They carry the same claims the API reads: sub, client_id, token_use and cognito:groups.
/// </summary>
public static class LocalTokens
{
    public const string Issuer = "hicleanarch-local";

    public static SymmetricSecurityKey SigningKey(string key) =>
        Encoding.UTF8.GetByteCount(key) >= 32
            ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
            : throw new InvalidOperationException("Auth:LocalSigningKey must be at least 32 bytes.");

    public static string Issue(string signingKey, string clientId, Guid userId, params string[] groups) =>
        Issue(signingKey, clientId, userId, "access", DateTime.UtcNow.AddHours(1), groups);

    /// <summary>Any kind of token, valid until any time: tests use it for ID tokens and expired ones.</summary>
    public static string Issue(string signingKey, string clientId, Guid userId, string tokenUse, DateTime expires, params string[] groups) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = expires.AddHours(-1),
            NotBefore = expires.AddHours(-1),
            Expires = expires,
            SigningCredentials = new SigningCredentials(SigningKey(signingKey), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["client_id"] = clientId,
                ["token_use"] = tokenUse,
                ["cognito:groups"] = groups,
            },
        });

    /// <summary>
    /// A machine token shaped like the one Cognito's client-credentials grant issues: the app client
    /// is both <c>sub</c> and <c>client_id</c>, <c>scope</c> lists the scopes, space-separated, and each
    /// token has its own <c>jti</c>. No groups.
    /// </summary>
    public static string IssueService(string signingKey, string clientId, DateTime expires, params string[] scopes) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = expires.AddHours(-1),
            NotBefore = expires.AddHours(-1),
            Expires = expires,
            SigningCredentials = new SigningCredentials(SigningKey(signingKey), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = clientId,
                ["client_id"] = clientId,
                ["token_use"] = "access",
                ["scope"] = string.Join(' ', scopes),
                ["jti"] = Guid.NewGuid().ToString(),
            },
        });
}
