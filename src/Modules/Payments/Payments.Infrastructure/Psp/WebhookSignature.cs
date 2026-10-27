using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// The PSP signs each webhook: <c>Psp-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.body"&gt;</c>,
/// with a secret only it and we know. We check the signature over the raw body, and refuse a
/// timestamp older than the tolerance, so a captured request cannot be replayed later. While the
/// PSP rotates its secret it may send several <c>v1</c> values: any one that matches passes.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "Psp-Signature";

    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string body, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Hmac(secret, t, body)}";
    }

    public static bool Verify(string secret, string? header, string body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var parts = header.Split(',').Select(p => p.Trim().Split('=', 2)).Where(p => p.Length == 2).ToList();
        var t = parts.FirstOrDefault(p => p[0] == "t")?[1];
        var signatures = parts.Where(p => p[0] == "v1").Select(p => p[1]).ToList();
        if (t is null || signatures.Count == 0
            || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Hmac(secret, t, body));
        return signatures.Any(v1 => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(v1)));
    }

    private static string Hmac(string secret, string t, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")));
}
