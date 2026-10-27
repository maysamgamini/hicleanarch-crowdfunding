using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using Testcontainers.PostgreSql;

namespace IntegrationTests;

/// <summary>
/// The hand-over of one schema to its service's migration role (lesson s5-l01), on the module with
/// the most kinds of object: Pledges, with its partitioned ledger and the functions that make and
/// detach partitions. The database is set up as on Aurora, where the owner is no superuser. The other
/// modules' schemas stay the owner's.
/// </summary>
public sealed class SchemaHandoverTests(Database shared) : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17").WithDatabase("crowdfunding")
        // Every statement in the server's log, where a plain-text password would show.
        .WithCommand("-c", "log_statement=all")
        .Build();

    private string As(string user, string password) =>
        new NpgsqlConnectionStringBuilder(_database.GetConnectionString()) { Username = user, Password = password }.ConnectionString;

    private string Owner => As("owner", "owner_test");

    private string Migrator => As("pledges_migrator", "pledges_migrator_test");

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();
        await Execute(_database.GetConnectionString(), """
            CREATE ROLE owner LOGIN PASSWORD 'owner_test' CREATEROLE;
            ALTER DATABASE crowdfunding OWNER TO owner;
            """);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Pledges_schema_with_its_partitions_and_functions_goes_to_its_migration_role_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Owner", Owner)
            .UseSetting("ConnectionStrings:Pledges", As("pledges_app", "pledges_test"))
            .UseSetting("ConnectionStrings:PledgesJobs", As("pledges_jobs", "pledges_jobs_test"))
            .UseSetting("Services:Payments:BaseAddress", "http://payments:8081")
            .UseSetting("ServiceIdentity:ClientId", Database.ServiceClientId)
            .UseSetting("Pledges:CloseEvery", "01:00:00")
            .UseSetting("Auth:ClientId", Database.ClientId)
            .UseSetting("Auth:LocalSigningKey", Database.SigningKey)
            // Payments' events reach the monolith through the broker (s4-l10): topics and queues of its own.
            .UseSetting("Events:Routes:Payments", "Broker")
            .UseSetting("Events:Producers:Payments:Pledges", "Broker")
            .UseSetting("Events:Producers:Payments:Campaigns", "Broker")
            .UseSetting("Events:Producers:Payments:Notifications", "Broker")
            // Notifications runs only in its own service (s5-l04): what it consumes goes to its queue.
            .UseSetting("Events:Routes:Notifications", "Broker")
            // Campaigns too, from s5-l09.
            .UseSetting("Events:Routes:Campaigns", "Broker")
            .Configure(shared.Provision().Settings));

        // The monolith's migrate step, as the owner.
        await host.Services.GetRequiredService<DatabaseMigrator>().Run(Owner, ct);
        Assert.True(await Count("SELECT count(*) FROM pg_inherits WHERE inhparent = 'pledges.pledge_ledger'::regclass") > 0);
        Assert.True(await Count("SELECT count(*) FROM pg_proc WHERE pronamespace = 'pledges'::regnamespace") > 0);

        // Another module's schema, as the owner left it (since s5-l09 the monolith migrates Pledges alone).
        await Execute(Owner, "CREATE SCHEMA IF NOT EXISTS other; CREATE TABLE IF NOT EXISTS other.things (id int)");
        await SchemaHandover.Run(Owner, "pledges", Migrator, ct);

        Assert.Equal(0, await Count("""
            SELECT count(*) FROM (
                SELECT nspowner AS owner FROM pg_namespace WHERE nspname = 'pledges'
                UNION ALL SELECT relowner FROM pg_class WHERE relnamespace = 'pledges'::regnamespace
                UNION ALL SELECT proowner FROM pg_proc WHERE pronamespace = 'pledges'::regnamespace
                UNION ALL SELECT typowner FROM pg_type WHERE typnamespace = 'pledges'::regnamespace
            ) o WHERE o.owner <> 'pledges_migrator'::regrole
            """));
        Assert.Equal(0, await Count("SELECT count(*) FROM pg_class WHERE relnamespace = 'other'::regnamespace AND relowner <> 'owner'::regrole"));

        // A migration's work, as the migration role: a column on the partitioned ledger (every
        // partition with it), a new month's partitions, a replaced function.
        await Execute(Migrator, """
            ALTER TABLE pledges.pledge_ledger ADD COLUMN note text;
            CREATE INDEX ix_ledger_note ON pledges.pledge_ledger (note);
            SELECT pledges.ensure_ledger_partitions((date_trunc('month', now()) + interval '24 months')::date, 1);
            """);

        // The service's own migrate step for Pledges, as the migration role: migrations, grants, the
        // ledger's revokes and the time-fence check.
        var pledgesOnly = new DatabaseMigrator(
            host.Services.GetServices<IModuleMigrator>().Where(m => m.Schema == "pledges"),
            host.Services.GetServices<ModuleDatabase>(),
            host.Services.GetRequiredService<ILogger<DatabaseMigrator>>());
        await pledgesOnly.RunAsSchemaOwner(Migrator, ct);

        // The module's login keeps exactly its rights: read and add to the ledger, never change it.
        await Execute(As("pledges_app", "pledges_test"), "SELECT count(*) FROM pledges.pledge_ledger");
        var change = await Assert.ThrowsAsync<PostgresException>(() =>
            Execute(As("pledges_app", "pledges_test"), "UPDATE pledges.pledge_ledger SET delta_minor = 0"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, change.SqlState);

        // The migration role has nothing in another module's schema.
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => Execute(Migrator, "ALTER TABLE other.things ADD COLUMN note text"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, foreign.SqlState);

        // The monolith's migrate step, as the owner, leaves the handed-over schema to its service.
        await host.Services.GetRequiredService<DatabaseMigrator>().Run(Owner, ct);
        Assert.Equal("pledges_migrator", await Text("SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'pledges'"));

        // Every role was created with its SCRAM verifier: the server logged every statement, and no
        // password a role logs in with is in that log.
        var (stdout, stderr) = await _database.GetLogsAsync(ct: ct);
        Assert.Contains("CREATE ROLE", stdout + stderr, StringComparison.Ordinal);
        foreach (var password in new[] { "pledges_migrator_test", "pledges_test", "pledges_jobs_test" })
        {
            Assert.DoesNotContain(password, stdout + stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_scram_verifier_is_what_postgres_stores_and_never_holds_the_password()
    {
        var verifier = ScramVerifier.For("pencil", Convert.FromBase64String("QSXCR+Q6sek8bf92"));

        // RFC 7677's example inputs (password "pencil", salt QSXCR+Q6sek8bf92, 4096 iterations); the keys
        // were computed apart from this code, with Python's hashlib and hmac.
        Assert.Equal("SCRAM-SHA-256$4096:QSXCR+Q6sek8bf92$FO+9jBb3MUukt6jJnzjPZOWc5ow/Pu6JtPyju0aqaE8=:qxJ1SbmSAi5EcS0J5Ck/cKAm/+Ixa+Kwp63f4OHDgzo=", verifier);
        Assert.DoesNotContain("pencil", verifier, StringComparison.Ordinal);
    }

    private async Task<string?> Text(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<long> Count(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal static class SettingsOnWebHost
{
    /// <summary>Each setting with UseSetting, which the host reads while it is built (connection strings, routes).</summary>
    public static IWebHostBuilder Configure(this IWebHostBuilder web, IReadOnlyDictionary<string, string?> settings)
    {
        foreach (var (key, value) in settings)
        {
            web.UseSetting(key, value);
        }

        return web;
    }
}
