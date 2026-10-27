using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Npgsql;

using Pledges.Contracts;

namespace Pledges.Infrastructure.Persistence;

/// <summary>Brings the Pledges schema up to date. Run by the host's <c>migrate</c> command, never at start-up.</summary>
internal sealed class PledgesMigrator(TimeFence fence, IConfiguration configuration) : IModuleMigrator
{
    /// <summary>No ledger month newer than this is ever detached; the database refuses it as well.</summary>
    public const int MinimumDetachMonths = 13;

    public string Module => PledgesModuleName.Value;

    public string Schema => PledgesDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        var options = PledgesDbOptions.Configure(new DbContextOptionsBuilder<PledgesDbContext>(), ownerConnectionString).Options;
        await using var db = new PledgesDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);

        // Archiving old months is the owner's job, so it happens here, in the deploy step, and only if set.
        if (configuration.GetValue<int?>("Pledges:Ledger:DetachAfterMonths") is { } months)
        {
            if (months < MinimumDetachMonths)
            {
                throw new InvalidOperationException(
                    $"Pledges:Ledger:DetachAfterMonths is {months}; ledger months are kept at least {MinimumDetachMonths} months.");
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pledges.detach_ledger_partitions_before((date_trunc('month', now() AT TIME ZONE 'UTC') - make_interval(months => {months}))::date)",
                cancellationToken);
        }
    }

    /// <summary>
    /// The ledger is append-only by its grants: the module's role may read it and add to it through the
    /// partitioned table, and has no rights at all on the monthly partitions themselves. It may run the
    /// functions that create partitions, never create a table, and never detach one. Activations are never deleted.
    /// </summary>
    public string AfterGrants(string quotedRole) => $$"""
        REVOKE UPDATE, DELETE, TRUNCATE ON pledges.pledge_ledger FROM {{quotedRole}};
        REVOKE UPDATE, DELETE, TRUNCATE ON pledges.pledge_activations FROM {{quotedRole}};
        DO $$
        DECLARE partition regclass;
        BEGIN
            FOR partition IN SELECT inhrelid::regclass FROM pg_inherits WHERE inhparent = 'pledges.pledge_ledger'::regclass LOOP
                EXECUTE 'REVOKE ALL ON ' || partition || ' FROM {{quotedRole.Replace("'", "''", StringComparison.Ordinal)}}';
            END LOOP;
        END $$;
        GRANT EXECUTE ON FUNCTION pledges.ensure_ledger_partitions(date, integer) TO {{quotedRole}};
        GRANT EXECUTE ON FUNCTION pledges.ledger_partition_months_ahead() TO {{quotedRole}};
        """;

    /// <summary>
    /// The time fence (lesson s3-l05) holds only if the database ends a request-path transaction at
    /// its limit. PostgreSQL 17 documents transaction_timeout; whether a managed engine honours it is
    /// checked here, on the real cluster, at every deploy: a session of the module's own role sleeps
    /// past the limit and must be ended in time, or the deploy fails.
    /// </summary>
    // PostgreSQL 17 ends a session whose transaction outlives transaction_timeout with this code.
    private const string TransactionTimeout = "25P04";

    public async Task Verify(IReadOnlyList<ModuleDatabase> roles, CancellationToken cancellationToken)
    {
        var requestPath = roles.Single(r => r.Module == Module);
        // A new login: a role's settings apply to sessions that start after ALTER ROLE, and a pooled
        // session opened earlier in this process would not have the limit.
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(requestPath.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var limit = fence.TransactionLimit;
        await using var sleep = new NpgsqlCommand($"SELECT pg_sleep({(limit + TimeSpan.FromSeconds(5)).TotalSeconds})", connection, transaction);
        try
        {
            await sleep.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == TransactionTimeout)
        {
            return; // the server ended it for its transaction_timeout, as the fence needs
        }

        throw new InvalidOperationException(
            $"The database did not end a {Module} transaction at its {limit.TotalSeconds:0.#} s transaction_timeout; the time fence cannot be relied on.");
    }
}
