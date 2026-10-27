using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Domain;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

namespace BuildingBlocks.Infrastructure;

/// <summary>What a successful change answers. Only successes are stored and replayed.</summary>
public sealed record Reply(int Status, object Body, string? Location = null);

/// <summary>
/// Runs a change at most once per caller and <c>Idempotency-Key</c>. The key row, the change and
/// the stored reply commit in one transaction of the module's own database context. A retry gets
/// the stored reply; the same key with a different request gets 422. A refused request rolls back,
/// key and all, so its retry is checked again from the start.
/// </summary>
public sealed class IdempotentRequests<TContext>(TContext db, IOptions<JsonOptions> json)
    where TContext : DbContext
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>
    /// A read-only look for this caller's key before work that must not be repeated is started (a
    /// PSP call, say): the stored reply if the first request with this key has finished, or the
    /// refusal <see cref="Run"/> would give (no key; the key used for another request). Null: nothing
    /// is stored yet, so the caller does its work and stores the reply with <see cref="Run"/>, which
    /// still settles a race between two first requests. It opens no transaction and writes nothing.
    /// </summary>
    public async Task<IResult?> TryReplay(HttpContext http, Guid callerId, object request)
    {
        if (KeyOf(http) is not { } key)
        {
            return MissingKey();
        }

        var stored = await db.Set<IdempotencyKey>().AsNoTracking()
            .SingleOrDefaultAsync(k => k.CallerId == callerId && k.Key == key, http.RequestAborted);
        return stored is null ? null : Replay(http, stored, Hash(http.Request, request));
    }

    public async Task<IResult> Run(HttpContext http, Guid callerId, object request, Func<CancellationToken, Task<Result<Reply>>> change)
    {
        var ct = http.RequestAborted;
        if (KeyOf(http) is not { } key)
        {
            return MissingKey();
        }

        var hash = Hash(http.Request, request);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var row = new IdempotencyKey { CallerId = callerId, Key = key, RequestHash = hash };
            db.Add(row);
            await db.SaveChangesAsync(ct); // a concurrent request with the same key waits here

            var result = await change(ct);
            if (!result.IsSuccess)
            {
                await transaction.RollbackAsync(ct);
                return result.Errors.ToProblem();
            }

            row.ReplyStatus = result.Value.Status;
            row.ReplyBody = JsonSerializer.Serialize(result.Value.Body, json.Value.SerializerOptions);
            row.ReplyLocation = result.Value.Location;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Send(result.Value.Status, row.ReplyBody, result.Value.Location);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } p
            && p.ConstraintName == $"pk_{IdempotencyKeyModel.Table}")
        {
            // The first request with this key has committed. This transaction is aborted, so the
            // stored reply is read in a new one.
            db.ChangeTracker.Clear();
            var stored = await db.Set<IdempotencyKey>().AsNoTracking()
                .SingleAsync(k => k.CallerId == callerId && k.Key == key, ct);
            return Replay(http, stored, hash);
        }
    }

    private static string? KeyOf(HttpContext http) =>
        http.Request.Headers[Header].ToString() is { Length: > 0 and <= 100 } key ? key : null;

    private static IResult MissingKey() =>
        new[] { Error.Validation("idempotency_key_required", $"Send an {Header} header of 1 to 100 characters.") }
            .ToProblem(StatusCodes.Status400BadRequest);

    private static IResult Replay(HttpContext http, IdempotencyKey stored, byte[] hash)
    {
        if (!stored.RequestHash.AsSpan().SequenceEqual(hash))
        {
            return new[] { Error.Validation("idempotency_key_reused", "This key was used for a different request.") }.ToProblem();
        }

        http.Response.Headers[ReplayedHeader] = "true";
        return Send(stored.ReplyStatus!.Value, stored.ReplyBody!, stored.ReplyLocation);
    }

    private static IResult Send(int status, string body, string? location) =>
        location is null
            ? Results.Text(body, "application/json", Encoding.UTF8, status)
            : new CreatedWithBody(location, body);

    private byte[] Hash(HttpRequest http, object request) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{http.Method} {http.Path}\n{JsonSerializer.Serialize(request, request.GetType(), json.Value.SerializerOptions)}"));

    private sealed class CreatedWithBody(string location, string body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.Location = location;
            return Results.Text(body, "application/json", Encoding.UTF8, StatusCodes.Status201Created).ExecuteAsync(httpContext);
        }
    }
}
