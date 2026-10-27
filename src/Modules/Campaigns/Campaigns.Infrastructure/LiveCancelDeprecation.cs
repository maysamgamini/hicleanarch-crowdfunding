using System.Diagnostics.Metrics;
using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Campaigns.Infrastructure;

/// <summary>
/// When the old path's LIVE cancel was deprecated, and when it stops (lesson s5-l05). Its contract
/// step, a 409 <c>funding_cancel_moved</c> with a link to Pledges' path, comes before Campaigns runs
/// in its own host (lesson s5-l08); the Sunset date must not be earlier than that release.
/// </summary>
public sealed class LiveCancelOptions
{
    public DateTimeOffset DeprecatedAt { get; set; } = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset SunsetAt { get; set; } = new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// The LIVE branch of <c>POST /v1/campaigns/{id}/cancel</c> still works in process, through parallel
/// change: every answer says it is deprecated (RFC 9745), when it ends (RFC 8594) and where its
/// successor is, and every call is counted, so the contract step waits until no one uses it.
/// </summary>
internal static class LiveCancelDeprecation
{
    private static readonly Meter Meter = new("CrowdFunding.Campaigns");

    private static readonly Counter<long> Calls = Meter.CreateCounter<long>(
        "campaigns.cancel.live_on_old_path", "{call}", "LIVE cancels still sent to Campaigns' deprecated path.");

    public static void Mark(HttpContext http, string campaignId)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<LiveCancelOptions>>().Value;
        var headers = http.Response.Headers;
        headers["Deprecation"] = "@" + options.DeprecatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        headers["Sunset"] = options.SunsetAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        headers.Link = $"</v1/campaigns/{campaignId}/funding/cancel>; rel=\"successor-version\"";
        Calls.Add(1);
    }
}
