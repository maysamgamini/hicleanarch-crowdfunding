using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Answers "the database cannot be reached" with <c>503</c> and <c>Retry-After</c>: a temporary
/// outage the client may wait out (RFC 9110, 15.6.4), not a bug. A failover, a cluster resuming
/// from a pause, or a full connection limit all end up here; anything else stays a <c>500</c>.
/// </summary>
public sealed class DatabaseUnavailableHandler : IExceptionHandler
{
    public const int RetryAfterSeconds = 5;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!IsUnavailable(exception))
        {
            return false;
        }

        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The database cannot be reached for a moment. Send the request again later; a change keeps its Idempotency-Key.",
            extensions: new Dictionary<string, object?> { ["reason"] = "database_unavailable" }).ExecuteAsync(httpContext);
        return true;
    }

    /// <summary>
    /// The connection itself failed (EF Core wraps it as a likely transient failure), or the server
    /// refused it for now: shutting down, starting up, or out of connections.
    /// </summary>
    public static bool IsUnavailable(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case PostgresException { SqlState: "57P01" or "57P02" or "57P03" or "53300" }:
                    return true;
                case PostgresException:
                    return false;
                case NpgsqlException:
                    return true;
                default:
                    break;
            }
        }

        return false;
    }
}
