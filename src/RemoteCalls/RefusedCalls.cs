using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;

using Microsoft.Extensions.Logging;

namespace CrowdFunding.RemoteCalls;

/// <summary>
/// Refusals from another service that are faults of contract or configuration, not outages: a 4xx
/// that carries no reason of the receiver's, such as model binding's 400 when the command's shape has
/// drifted, the bare 404 of the wrong port, or the token check's 401 and 403. Waiting does not cure
/// them, so they are not retried and not answered with a wait. Each is counted
/// (<c>remote_calls.refused</c>, by service and status, which has an alarm) and logged at error, once a
/// minute per service and status, so a deploy that breaks a call is seen at once without one log line
/// per request.
/// </summary>
public sealed partial class RefusedCalls(TimeProvider clock, ILogger<RefusedCalls> logger)
{
    public const string MeterName = "CrowdFunding.RemoteCalls";

    /// <summary>At most one error log per service and status in this time.</summary>
    public static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(1);

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Refused = Meter.CreateCounter<long>(
        "remote_calls.refused", description: "Calls another service refused with a 4xx that names no reason: a contract or configuration fault");

    private readonly ConcurrentDictionary<(string Service, int Status), DateTimeOffset> _logged = new();

    public void Record(string service, string operation, HttpStatusCode status)
    {
        Refused.Add(1, new KeyValuePair<string, object?>("service", service), new KeyValuePair<string, object?>("status", (int)status));
        var now = clock.GetUtcNow();
        var key = (service, (int)status);
        var last = _logged.GetOrAdd(key, DateTimeOffset.MinValue);
        if (now - last >= LogEvery && _logged.TryUpdate(key, now, last))
        {
            LogRefused(logger, service, operation, (int)status);
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "{Service} refused {Operation} with {Status} and no reason: a contract or configuration fault (a wrong address, client, scope or command shape), not an outage. Logged once a minute; remote_calls.refused counts each")]
    private static partial void LogRefused(ILogger logger, string service, string operation, int status);
}
