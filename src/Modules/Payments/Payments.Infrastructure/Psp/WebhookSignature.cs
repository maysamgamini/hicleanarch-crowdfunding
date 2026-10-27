using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// The PSP signs each webhook: <c>Psp-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.body"&gt;</c>,
/// with a secret only it and we know. We check the signature over the raw body, and refuse a
/// timestamp older than the tolerance, so a captured request cannot be replayed later.
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

        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        if (!parts.TryGetValue("t", out var t) || !parts.TryGetValue("v1", out var v1)
            || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hmac(secret, t, body)), Encoding.ASCII.GetBytes(v1));
    }

    private static string Hmac(string secret, string t, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")));
}
