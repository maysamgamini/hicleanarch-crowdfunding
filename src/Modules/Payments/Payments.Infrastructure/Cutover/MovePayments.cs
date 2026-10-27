using System.Globalization;

using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using Payments.Application;
using Payments.Contracts;
using Payments.Infrastructure.Persistence;

namespace Payments.Infrastructure.Cutover;

/// <summary>
/// <c>move-payments &lt;step&gt;</c>: moving Payments' tables from the shared cluster to a clone of it
/// (lesson s4-a06, fence, clone, switch). One step per run, in this order: pause, fence, (the clone,
/// made by OpenTofu once the cluster's latest restorable time is past the fence), prune, verify,
/// switch, (Payments rolled onto the new secret), resume; rollback at any point before resume;
/// drop-old a week after. Every step checks where things stand first, so running it again does
/// nothing new, and logs what it did.
/// </summary>
public static class MovePaymentsCommand
{
    public static readonly IReadOnlyList<string> Steps = ["pause", "fence", "prune", "verify", "switch", "resume", "rollback", "drop-old"];

    /// <summary>Runs one step. The exit code: 0 done or already done, 1 refused or failed, 2 no such step.</summary>
    public static async Task<int> MovePayments(this IServiceProvider services, string? step, CancellationToken cancellationToken)
    {
        var move = ActivatorUtilities.CreateInstance<PaymentsMove>(services);
        Func<CancellationToken, Task<bool>>? run = step switch
        {
            "pause" => move.Pause,
            "fence" => move.Fence,
            "prune" => move.Prune,
            "verify" => move.Verify,
            "switch" => move.Switch,
            "resume" => move.Resume,
            "rollback" => move.Rollback,
            "drop-old" => move.DropOld,
            _ => null,
        };
        if (run is null)
        {
            await Console.Error.WriteLineAsync($"Usage: move-payments {string.Join(" | ", Steps)}");
            return 2;
        }

        return await run(cancellationToken) ? 0 : 1;
    }
}

internal sealed partial class PaymentsMove(
    IConfiguration configuration,
    IPaymentsPause pause,
    SettlementOptions settlement,
    TimeProvider clock,
    ILoggerFactory loggers,
    ILogger<PaymentsMove> logger)
{
    private const string Schema = PaymentsDbContext.Schema;

    private const string FenceNote = "payments-move: fenced at ";

    /// <summary>The shared cluster, as its owner.</summary>
    private string SourceOwner => Required("Owner");

    /// <summary>Payments' own cluster (the clone), as its master user.</summary>
    private string TargetOwner => Required("PaymentsOwnOwner");

    /// <summary>Payments' login on its own cluster, with the new password: what the switch opens it for.</summary>
    private string TargetLogin => Required("PaymentsOwn");

    /// <summary>Payments' role: the same name on both clusters, since the clone copies the roles.</summary>
    private string Role => new NpgsqlConnectionStringBuilder(Required("Payments")).Username
        ?? throw new InvalidOperationException("ConnectionStrings:Payments has no user name.");

    private TimeSpan KeepOldFor => configuration.GetValue("Payments:Move:KeepOldFor", TimeSpan.FromDays(7));

    private int ChunkRows => configuration.GetValue("Payments:Move:ChunkRows", TableDigests.DefaultChunkRows);

    private TimeSpan SessionsEndWithin => configuration.GetValue("Payments:Move:SessionsEndWithin", TimeSpan.FromSeconds(30));

    /// <summary>
    /// Sets the flag, then waits until no settlement job is leased: the workers give theirs back
    /// when they see the flag, and a lease of a worker that died runs out within its length.
    /// </summary>
    public async Task<bool> Pause(CancellationToken ct)
    {
        if (!pause.IsShared)
        {
            LogFlagNotShared(logger);
        }

        await pause.Set(ct);
        LogPaused(logger);
        await using var source = await Open(SourceOwner, ct);
        var until = clock.GetUtcNow() + settlement.Lease + TimeSpan.FromSeconds(10);
        while (true)
        {
            var held = await Scalar<long>(source, $"SELECT count(*) FROM {Schema}.settlement_jobs WHERE lease_owner IS NOT NULL AND lease_until > now()", ct);
            if (held == 0)
            {
                LogNoLeases(logger);
                return true;
            }

            if (clock.GetUtcNow() > until)
            {
                LogLeasesHeld(logger, held);
                return false;
            }

            LogWaitingForLeases(logger, held);
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
    }

    /// <summary>
    /// On the shared cluster, Payments' role loses its login and its write rights, and every session
    /// it still has is ended; a write that passed the flag check now fails instead of committing after
    /// the copy. The fence's time, on the database's clock, is kept as the role's comment.
    /// </summary>
    public async Task<bool> Fence(CancellationToken ct)
    {
        if (!await pause.IsSet(ct))
        {
            LogPauseFirst(logger);
            return false;
        }

        await using var source = await Open(SourceOwner, ct);
        if (await CanLogIn(source, ct) is null)
        {
            LogNoRole(logger, Role);
            return false;
        }

        var role = Sql.Quote(Role);
        await Execute(source, $"""
            ALTER ROLE {role} NOLOGIN;
            REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON ALL TABLES IN SCHEMA {Schema} FROM {role};
            REVOKE USAGE, UPDATE ON ALL SEQUENCES IN SCHEMA {Schema} FROM {role};
            """, ct);
        LogFenced(logger, Role);
        if (!await EndSessions(source, ct))
        {
            return false;
        }

        var fencedAt = await FencedAt(source, ct);
        if (fencedAt is null)
        {
            fencedAt = await Scalar<DateTime>(source, "SELECT now()", ct);
            await Execute(source, $"COMMENT ON ROLE {role} IS {Sql.Literal(FenceNote + fencedAt.Value.ToString("O", CultureInfo.InvariantCulture))};", ct);
        }

        LogFenceHolds(logger, fencedAt.Value);
        return true;
    }

    /// <summary>
    /// On the clone, which starts as a copy of everyone's data: every other module's schema and login
    /// role is dropped, in one transaction. The new master password comes from OpenTofu.
    /// </summary>
    public async Task<bool> Prune(CancellationToken ct)
    {
        if (PaymentsDatabase.SameDatabase(SourceOwner, TargetOwner))
        {
            LogSameDatabase(logger);
            return false;
        }

        await using var target = await Open(TargetOwner, ct);
        if (await Scalar<long>(target, $"SELECT count(*) FROM pg_namespace WHERE nspname = '{Schema}'", ct) == 0)
        {
            LogNoSchemaOnClone(logger);
            return false;
        }

        var schemas = await OtherSchemas(target, ct);
        var roles = await Strings(target, """
            SELECT DISTINCT r.rolname FROM pg_roles r CROSS JOIN pg_namespace n
            WHERE n.nspname::text = ANY (@schemas) AND has_schema_privilege(r.oid, n.oid, 'USAGE')
              AND r.rolname <> current_user AND r.rolname <> @role AND NOT r.rolsuper AND r.rolname !~ '^(pg_|rds)'
            ORDER BY 1
            """, ct, ("schemas", schemas.ToArray()), ("role", Role));
        if (schemas.Count == 0 && roles.Count == 0)
        {
            LogAlreadyPruned(logger);
            return true;
        }

        await Execute(target, string.Concat(
            schemas.Select(s => $"DROP SCHEMA {Sql.Quote(s)} CASCADE;\n")
                .Concat(roles.Select(r => $"DROP OWNED BY {Sql.Quote(r)};\nDROP ROLE {Sql.Quote(r)};\n"))), ct);
        LogPruned(logger, schemas, roles);
        return true;
    }

    /// <summary>Every table of the schema, compared row by row in key order on the fenced source and on the clone.</summary>
    public async Task<bool> Verify(CancellationToken ct)
    {
        await using var source = await Open(SourceOwner, ct);
        await using var target = await Open(TargetOwner, ct);
        var tables = await TableDigests.Compare(source, target, Schema, ChunkRows, ct);
        foreach (var table in tables)
        {
            if (table.Matches)
            {
                LogTableMatches(logger, table.Table, table.SourceRows, table.Chunks);
            }
            else if (!table.OnBothSides)
            {
                LogTableOnOneSide(logger, table.Table);
            }
            else if (table.FirstDifferingChunk is { } chunk)
            {
                LogTableDiffers(logger, table.Table, table.SourceRows, table.TargetRows, chunk);
            }
        }

        var matching = tables.Count(t => t.Matches);
        LogVerified(logger, matching, tables.Count);
        return tables.Count > 0 && matching == tables.Count;
    }

    /// <summary>
    /// Opens the clone to Payments, only while the source is fenced, the clone holds nothing else and
    /// every digest matches: the deploy's own grants, then the login with its new password. Rolling
    /// Payments onto the new connection secret follows (an ECS deployment; tasks read secrets at start).
    /// </summary>
    public async Task<bool> Switch(CancellationToken ct)
    {
        await using var target = await Open(TargetOwner, ct);
        if (await CanLogIn(target, ct) == true)
        {
            LogAlreadySwitched(logger);
            return true;
        }

        await using (var source = await Open(SourceOwner, ct))
        {
            if (await CanLogIn(source, ct) != false)
            {
                LogNotFenced(logger);
                return false;
            }
        }

        if ((await OtherSchemas(target, ct)).Count > 0)
        {
            LogNotPruned(logger);
            return false;
        }

        if (!await Verify(ct))
        {
            LogNotSwitched(logger);
            return false;
        }

        var login = new NpgsqlConnectionStringBuilder(TargetLogin);
        if (login.Username != Role || string.IsNullOrEmpty(login.Password))
        {
            LogWrongLogin(logger, Role);
            return false;
        }

        await Grants(TargetOwner, TargetLogin, ct);
        var role = Sql.Quote(Role);
        await Execute(target, $"ALTER ROLE {role} LOGIN PASSWORD {Sql.Literal(ScramVerifier.For(login.Password))};\nCOMMENT ON ROLE {role} IS NULL;", ct);
        LogSwitched(logger);
        return true;
    }

    /// <summary>Clears the flag, once Payments has a database it may write: the clone, or the shared one after a rollback.</summary>
    public async Task<bool> Resume(CancellationToken ct)
    {
        await using var source = await Open(SourceOwner, ct);
        if (await CanLogIn(source, ct) == false)
        {
            await using var target = await Open(TargetOwner, ct);
            if (await CanLogIn(target, ct) != true)
            {
                LogNowhereToWrite(logger);
                return false;
            }
        }

        await pause.Clear(ct);
        LogResumed(logger);
        return true;
    }

    /// <summary>
    /// Before resume only: closes the clone to Payments (if it is still there and has not moved on),
    /// gives Payments' role its login and the deploy's grants back on the shared cluster, and clears
    /// the flag. OpenTofu then deletes the clone and its secrets.
    /// </summary>
    public async Task<bool> Rollback(CancellationToken ct)
    {
        await using var source = await Open(SourceOwner, ct);
        if (await CanLogIn(source, ct) == false && !await pause.IsSet(ct))
        {
            LogAlreadyResumed(logger);
            return false;
        }

        if (configuration.GetConnectionString("PaymentsOwnOwner") is { Length: > 0 })
        {
            if (!await CloseClone(ct))
            {
                return false;
            }
        }

        await Grants(SourceOwner, Required("Payments"), ct);
        var role = Sql.Quote(Role);
        await Execute(source, $"ALTER ROLE {role} LOGIN;\nCOMMENT ON ROLE {role} IS NULL;", ct);
        await pause.Clear(ct);
        LogRolledBack(logger);
        return true;
    }

    /// <summary>A week after resume: the old schema and the old login leave the shared cluster.</summary>
    public async Task<bool> DropOld(CancellationToken ct)
    {
        await using var source = await Open(SourceOwner, ct);
        var canLogIn = await CanLogIn(source, ct);
        if (canLogIn is null && await Scalar<long>(source, $"SELECT count(*) FROM pg_namespace WHERE nspname = '{Schema}'", ct) == 0)
        {
            LogAlreadyDropped(logger);
            return true;
        }

        await using var target = await Open(TargetOwner, ct);
        if (canLogIn != false || await pause.IsSet(ct) || await CanLogIn(target, ct) != true)
        {
            LogNotMovedYet(logger);
            return false;
        }

        if (await FencedAt(source, ct) is not { } fencedAt)
        {
            LogNotMovedYet(logger);
            return false;
        }

        var keepUntil = fencedAt + KeepOldFor;
        if (clock.GetUtcNow().UtcDateTime < keepUntil)
        {
            LogTooSoon(logger, keepUntil);
            return false;
        }

        var role = Sql.Quote(Role);
        await Execute(source, $"DROP SCHEMA IF EXISTS {Schema} CASCADE;\nDROP OWNED BY {role};\nDROP ROLE {role};", ct);
        LogDropped(logger, Role);
        return true;
    }

    private async Task<bool> CloseClone(CancellationToken ct)
    {
        NpgsqlConnection target;
        try
        {
            target = await Open(TargetOwner, ct);
        }
        catch (Exception e) when (e is NpgsqlException or System.Net.Sockets.SocketException)
        {
            LogNoClone(logger, e.Message);
            return true;
        }

        await using (target)
        {
            if (await CanLogIn(target, ct) == true && !await Verify(ct))
            {
                LogCloneMovedOn(logger);
                return false;
            }

            if (await CanLogIn(target, ct) is not null)
            {
                await Execute(target, $"ALTER ROLE {Sql.Quote(Role)} NOLOGIN;", ct);
                if (!await EndSessions(target, ct))
                {
                    return false;
                }
            }

            LogCloneClosed(logger);
            return true;
        }
    }

    // The grants the deploy step gives Payments' role, from the same migrator: the clone's copy was
    // taken fenced, and a rollback must give back exactly what the deploy gave, the ledger's revokes included.
    private Task Grants(string owner, string login, CancellationToken ct) =>
        new DatabaseMigrator(
                [new PaymentsMigrator()],
                [new ModuleDatabase(PaymentsModuleName.Value, Schema, login)],
                loggers.CreateLogger<DatabaseMigrator>())
            .Run(owner, ct);

    // pg_terminate_backend only signals a session; the fence holds once none is left.
    private async Task<bool> EndSessions(NpgsqlConnection connection, CancellationToken ct)
    {
        var until = clock.GetUtcNow() + SessionsEndWithin;
        while (true)
        {
            var ended = await Scalar<long>(connection,
                "SELECT count(*) FILTER (WHERE pg_terminate_backend(pid)) FROM pg_stat_activity WHERE usename = @role AND pid <> pg_backend_pid()",
                ct, ("role", Role));
            if (ended == 0)
            {
                return true;
            }

            LogSessionsEnded(logger, ended);
            if (clock.GetUtcNow() > until)
            {
                LogSessionsRemain(logger);
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), clock, ct);
        }
    }

    private async Task<bool?> CanLogIn(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT rolcanlogin FROM pg_roles WHERE rolname = @role", connection);
        command.Parameters.AddWithValue("role", Role);
        return await command.ExecuteScalarAsync(ct) as bool?;
    }

    private async Task<DateTime?> FencedAt(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT shobj_description(oid, 'pg_authid') FROM pg_roles WHERE rolname = @role", connection);
        command.Parameters.AddWithValue("role", Role);
        return await command.ExecuteScalarAsync(ct) is string note && note.StartsWith(FenceNote, StringComparison.Ordinal)
            ? DateTime.Parse(note[FenceNote.Length..], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : null;
    }

    // Every schema the owner made except Payments' own: on the clone, everyone else's. The system's
    // own schemas are left out by name, for a server where the owner is also the bootstrap superuser.
    private static Task<IReadOnlyList<string>> OtherSchemas(NpgsqlConnection connection, CancellationToken ct) =>
        Strings(connection, $"""
            SELECT nspname FROM pg_namespace
            WHERE nspowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
              AND nspname NOT IN ('{Schema}', 'public', 'information_schema') AND nspname !~ '^pg_'
            ORDER BY 1
            """, ct);

    private string Required(string name) => configuration.GetConnectionString(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"ConnectionStrings:{name} is not set.");

    private static async Task<NpgsqlConnection> Open(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IReadOnlyList<string>> Strings(NpgsqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Valkey: the pause flag reaches this process only; the fence alone stops the other tasks")]
    private static partial void LogFlagNotShared(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pause: the flag is set; Payments answers 503 and its jobs take no work")]
    private static partial void LogPaused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pause: no settlement job is leased")]
    private static partial void LogNoLeases(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pause: waiting for {Held} settlement leases to be given back or run out")]
    private static partial void LogWaitingForLeases(ILogger logger, long held);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pause: {Held} settlement leases are still held after the lease's length; not paused cleanly")]
    private static partial void LogLeasesHeld(ILogger logger, long held);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fence refused: set the pause flag first (move-payments pause)")]
    private static partial void LogPauseFirst(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The role {Role} does not exist on the shared cluster")]
    private static partial void LogNoRole(ILogger logger, string role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fence: {Role} cannot log in and has no write rights on the shared cluster")]
    private static partial void LogFenced(ILogger logger, string role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ended {Sessions} sessions of Payments' role")]
    private static partial void LogSessionsEnded(ILogger logger, long sessions);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payments' role still has sessions; the fence does not hold yet")]
    private static partial void LogSessionsRemain(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fence holds since {FencedAt:O}; clone the shared cluster once its latest restorable time is later")]
    private static partial void LogFenceHolds(ILogger logger, DateTime fencedAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Prune refused: the clone's connection names the shared database")]
    private static partial void LogSameDatabase(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The clone has no payments schema")]
    private static partial void LogNoSchemaOnClone(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prune: the clone already holds only Payments")]
    private static partial void LogAlreadyPruned(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prune: dropped the schemas {Schemas} and the roles {Roles} from the clone")]
    private static partial void LogPruned(ILogger logger, IReadOnlyList<string> schemas, IReadOnlyList<string> roles);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verify: {Table} matches, {Rows} rows in {Chunks} chunks")]
    private static partial void LogTableMatches(ILogger logger, string table, long rows, int chunks);

    [LoggerMessage(Level = LogLevel.Error, Message = "Verify: {Table} exists on one side only")]
    private static partial void LogTableOnOneSide(ILogger logger, string table);

    [LoggerMessage(Level = LogLevel.Error, Message = "Verify: {Table} differs ({SourceRows} rows on the shared cluster, {TargetRows} on the clone), first in chunk {Chunk}")]
    private static partial void LogTableDiffers(ILogger logger, string table, long sourceRows, long targetRows, int chunk);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verify: {Matching} of {Tables} tables match")]
    private static partial void LogVerified(ILogger logger, int matching, int tables);

    [LoggerMessage(Level = LogLevel.Information, Message = "Switch: Payments can already log in to the clone")]
    private static partial void LogAlreadySwitched(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Switch refused: Payments' role can still log in to the shared cluster")]
    private static partial void LogNotFenced(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Switch refused: the clone still holds other modules' schemas (move-payments prune)")]
    private static partial void LogNotPruned(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Switch refused: the digests differ")]
    private static partial void LogNotSwitched(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Switch refused: ConnectionStrings:PaymentsOwn must log in as {Role}, with a password")]
    private static partial void LogWrongLogin(ILogger logger, string role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Switch: Payments may log in to the clone; roll Payments onto the PaymentsOwn secret, then resume")]
    private static partial void LogSwitched(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Resume refused: Payments is fenced on the shared cluster and cannot log in to the clone; switch or roll back")]
    private static partial void LogNowhereToWrite(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resume: the flag is cleared")]
    private static partial void LogResumed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rollback refused: Payments has resumed on the clone; going back is a dump and restore during another stop")]
    private static partial void LogAlreadyResumed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rollback: no clone to close ({Reason})")]
    private static partial void LogNoClone(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rollback refused: the clone has changed since the switch")]
    private static partial void LogCloneMovedOn(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rollback: Payments cannot log in to the clone; delete it with OpenTofu")]
    private static partial void LogCloneClosed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rollback: Payments has its login and rights back on the shared cluster, and the flag is cleared")]
    private static partial void LogRolledBack(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drop old: the shared cluster holds no Payments schema or login")]
    private static partial void LogAlreadyDropped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Drop old refused: Payments has not moved (fenced here, resumed, and open on the clone)")]
    private static partial void LogNotMovedYet(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Drop old refused: the old schema is kept until {KeepUntil:O}")]
    private static partial void LogTooSoon(ILogger logger, DateTime keepUntil);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drop old: dropped the payments schema and the role {Role} from the shared cluster")]
    private static partial void LogDropped(ILogger logger, string role);
}
