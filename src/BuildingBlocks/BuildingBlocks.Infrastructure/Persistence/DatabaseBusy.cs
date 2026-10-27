using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The database was busy for this request: a lock was not free within its short wait, or the
/// transaction ran past its time limit. Nothing was changed; the client tries again with the same
/// idempotency key.
/// </summary>
public sealed class DatabaseBusyException(string reason, Exception inner)
    : Exception($"The database was busy ({reason}); nothing was changed.", inner)
{
    public string Reason { get; } = reason;
}

public static class DatabaseBusy
{
    /// <summary>
    /// Runs one statement under a short lock wait. <c>SET LOCAL</c> lasts until the transaction ends.
    /// A wait that runs out ends the statement with <c>55P03</c>, the transaction rolls back, and the
    /// database session stays usable, unlike a <c>transaction_timeout</c>, which ends the session.
    /// </summary>
    public static async Task<T> WithLockTimeout<T>(Func<string, Task> execute, TimeSpan wait, string reason, Func<Task<T>> statement)
    {
        await execute($"SET LOCAL lock_timeout = '{(int)wait.TotalMilliseconds}ms'");
        try
        {
            return await statement();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new DatabaseBusyException(reason, e);
        }
    }
}

/// <summary>Answers a busy database with <c>409</c>, the reason, and <c>Retry-After: 1</c>.</summary>
public sealed class DatabaseBusyHandler : IExceptionHandler
{
    // PostgreSQL 17 ends a session whose transaction outlives transaction_timeout with this code.
    private const string TransactionTimeout = "25P04";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var reason = exception switch
        {
            DatabaseBusyException busy => busy.Reason,
            _ when EndedByTransactionTimeout(exception) => "try_again",
            _ => null,
        };
        if (reason is null)
        {
            return false;
        }

        httpContext.Response.Headers.RetryAfter = "1";
        await Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The request met a busy moment and changed nothing. Send it again with the same Idempotency-Key.",
            extensions: new Dictionary<string, object?> { ["reason"] = reason }).ExecuteAsync(httpContext);
        return true;
    }

    // EF Core wraps it (DbUpdateException from SaveChanges), so the whole chain is searched.
    private static bool EndedByTransactionTimeout(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException { SqlState: TransactionTimeout })
            {
                return true;
            }
        }

        return false;
    }
}
