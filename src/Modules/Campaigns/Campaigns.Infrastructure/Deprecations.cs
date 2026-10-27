using System.Globalization;

using Microsoft.AspNetCore.Http;

namespace Campaigns.Infrastructure;

/// <summary>
/// The three headers of a deprecated part of the API, in parallel change: when it was deprecated
/// (Deprecation, RFC 9745), when it stops (Sunset, RFC 8594), and where its successor is (Link,
/// rel="successor-version"). Used by the old path's LIVE cancel (s5-l05) and the reply's totals (s5-l06).
/// </summary>
internal static class Deprecations
{
    public static void Mark(HttpContext http, DateTimeOffset deprecatedAt, DateTimeOffset sunsetAt, string successor)
    {
        var headers = http.Response.Headers;
        headers["Deprecation"] = "@" + deprecatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        headers["Sunset"] = sunsetAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        headers.Link = $"<{successor}>; rel=\"successor-version\"";
    }
}
