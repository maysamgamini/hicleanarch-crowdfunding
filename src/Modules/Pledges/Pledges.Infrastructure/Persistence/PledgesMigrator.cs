using BuildingBlocks.Infrastructure.Persistence;

using System.Diagnostics;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Pledges.Contracts;

namespace Pledges.Infrastructure.Persistence;

/// <summary>Brings the Pledges schema up to date. Run by the host's <c>migrate</c> command, never at start-up.</summary>
internal sealed class PledgesMigrator(TimeFence fence) : IModuleMigrator
{
    public string Module => PledgesModuleName.Value;

    public string Schema => PledgesDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        var options = PledgesDbOptions.Configure(new DbContextOptionsBuilder<PledgesDbContext>(), ownerConnectionString).Options;
        await using var db = new PledgesDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The ledger is append-only by its grants: the module's role may read it and add to it, nothing else.</summary>
    public string AfterGrants(string quotedRole) =>
        $"REVOKE UPDATE, DELETE, TRUNCATE ON pledges.pledge_ledger FROM {quotedRole};";

    /// <summary>
    /// The time fence (lesson s3-l05) holds only if the database ends a request-path transaction at
    /// its limit. PostgreSQL 17 documents transaction_timeout; whether a managed engine honours it is
    /// checked here, on the real cluster, at every deploy: a session of the module's own role sleeps
    /// past the limit and must be ended in time, or the deploy fails.
    /// </summary>
    public async Task Verify(IReadOnlyList<ModuleDatabase> roles, CancellationToken cancellationToken)
    {
        var requestPath = roles.Single(r => r.Module == Module);
        await using var connection = new NpgsqlConnection(requestPath.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var limit = fence.TransactionLimit;
        await using var sleep = new NpgsqlCommand($"SELECT pg_sleep({(limit + TimeSpan.FromSeconds(2)).TotalSeconds})", connection, transaction);
        var watch = Stopwatch.StartNew();
        try
        {
            await sleep.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (NpgsqlException) when (watch.Elapsed < limit + TimeSpan.FromSeconds(1))
        {
            return; // ended at the limit, as the fence needs
        }

        throw new InvalidOperationException(
            $"The database did not end a {Module} transaction at its {limit.TotalSeconds:0.#} s transaction_timeout; the time fence cannot be relied on.");
    }
}
