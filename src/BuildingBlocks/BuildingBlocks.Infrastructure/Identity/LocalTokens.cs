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
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey(signingKey), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["client_id"] = clientId,
                ["token_use"] = "access",
                ["cognito:groups"] = groups,
            },
        });
}
