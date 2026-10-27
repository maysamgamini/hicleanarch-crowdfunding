using System.Security.Cryptography;
using System.Text;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// A role's password as PostgreSQL stores it, <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>
/// (RFC 5802, RFC 7677), computed here, so <c>CREATE ROLE … PASSWORD</c> sends the verifier and never the
/// password itself: a statement the server logs (log_statement = 'ddl', or an error's statement text)
/// holds nothing a client could log in with. PostgreSQL stores a value in this form as it is.
/// </summary>
public static class ScramVerifier
{
    public const int Iterations = 4096;

    public static string For(string password, byte[]? salt = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        salt ??= RandomNumberGenerator.GetBytes(16);
        // SASLprep is the identity for the ASCII passwords the deploy generates.
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        return $"SCRAM-SHA-256${Iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
