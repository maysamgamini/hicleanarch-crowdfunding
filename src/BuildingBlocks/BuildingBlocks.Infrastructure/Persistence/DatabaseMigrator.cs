using Microsoft.Extensions.Logging;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The deploy step that prepares the database, run once per release before the new tasks start.
/// For each module: make sure its login role and its schema exist, apply its migrations as the
/// owner, then let the module's role use its own schema, and nothing else: not the <c>public</c>
/// schema, not temporary tables, and not its schema's migration history, which only the owner writes.
/// </summary>
public sealed partial class DatabaseMigrator(
    IEnumerable<IModuleMigrator> migrators,
    IEnumerable<ModuleDatabase> databases,
    ILogger<DatabaseMigrator> logger)
{
    public async Task Run(string ownerConnectionString, CancellationToken cancellationToken)
    {
        foreach (var migrator in migrators)
        {
            // A schema handed to its service's migration role (SchemaHandover) is that service's to
            // migrate: the monolith, which still runs the module until it leaves, no longer touches it.
            if (await HandedOverTo(ownerConnectionString, migrator.Schema, cancellationToken) is { } owner)
            {
                LogHandedOver(logger, migrator.Module, migrator.Schema, owner);
                continue;
            }

            // Every role that logs in to this module's schema: the module's own, and any other it uses.
            var roles = databases.Where(d => d.Schema == migrator.Schema).ToList();
            foreach (var role in roles)
            {
                await PrepareRoleAndSchema(ownerConnectionString, role, cancellationToken);
            }

            await migrator.Migrate(ownerConnectionString, cancellationToken);
            // The grants, the module's own revokes and the roles' settings commit together, so a role
            // never holds, even for a moment, a right the module takes away (such as changing its ledger).
            await Execute(ownerConnectionString,
                string.Concat(roles.Select(r => GrantTables(r) + migrator.AfterGrants(Quote(r.Role)) + Settings(r))),
                cancellationToken);
            await migrator.Verify(roles, cancellationToken);

            LogMigrated(logger, migrator.Module, migrator.Schema);
        }
    }

    /// <summary>
    /// A service's own migrate step, after <see cref="SchemaHandover"/>: logged in as its migration role,
    /// which owns its schema and every object in it, but may not create roles, schemas or settings.
    /// It applies the migrations, then grants the module's roles their tables and takes back what the
    /// module takes back, as <see cref="Run"/> does. The roles and their settings stay as the owner
    /// left them.
    /// </summary>
    public async Task RunAsSchemaOwner(string migratorConnectionString, CancellationToken cancellationToken)
    {
        foreach (var migrator in migrators)
        {
            var roles = databases.Where(d => d.Schema == migrator.Schema).ToList();
            await migrator.Migrate(migratorConnectionString, cancellationToken);
            await Execute(migratorConnectionString,
                string.Concat(roles.Select(r => GrantTables(r) + migrator.AfterGrants(Quote(r.Role)))),
                cancellationToken);
            await migrator.Verify(roles, cancellationToken);

            LogMigrated(logger, migrator.Module, migrator.Schema);
        }
    }

    /// <summary>The schemas this host's modules keep.</summary>
    public IReadOnlyList<string> Schemas => [.. migrators.Select(m => m.Schema).Distinct()];

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrated {Module} in schema {Schema}")]
    private static partial void LogMigrated(ILogger logger, string module, string schema);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped {Module}: schema {Schema} belongs to {Owner}, whose service migrates it")]
    private static partial void LogHandedOver(ILogger logger, string module, string schema, string owner);

    /// <summary>The schema's owner when it is not the connecting role (the schema was handed over), otherwise null.</summary>
    private static async Task<string?> HandedOverTo(string owner, string schema, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = @schema AND nspowner <> (SELECT oid FROM pg_roles WHERE rolname = current_user)",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task PrepareRoleAndSchema(string owner, ModuleDatabase module, CancellationToken ct)
    {
        var role = Quote(module.Role);
        var schema = Quote(module.Schema);
        await Execute(owner, $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = {Literal(module.Role)}) THEN
                    CREATE ROLE {role} LOGIN PASSWORD {Literal(ScramVerifier.For(module.Password))};
                END IF;
            END $$;
            CREATE SCHEMA IF NOT EXISTS {schema};
            -- Every role holds what PUBLIC holds: USAGE on schema public and TEMPORARY on the database
            -- by default. Revoking from one role would change nothing; revoking from PUBLIC does.
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            DO $$ BEGIN EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC', current_database()); END $$;
            GRANT USAGE ON SCHEMA {schema} TO {role};
            """, ct);
    }

    private static string GrantTables(ModuleDatabase module)
    {
        var role = Quote(module.Role);
        var schema = Quote(module.Schema);
        return $"""
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO {role};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {role};
            REVOKE ALL ON {schema}.{Quote(MigrationsHistory)} FROM {role};

            """;
    }

    // A setting on a role applies to every session that logs in as it afterwards. The owner sets it
    // here, in the deploy step: a module's own role may not change its own limits.
    private static string Settings(ModuleDatabase module) =>
        string.Concat(module.RoleSettings.Select(setting => $"ALTER ROLE {Quote(module.Role)} SET {setting};\n"));

    private static async Task Execute(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The migration history table every module keeps in its own schema.</summary>
    public const string MigrationsHistory = "__ef_migrations";

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
