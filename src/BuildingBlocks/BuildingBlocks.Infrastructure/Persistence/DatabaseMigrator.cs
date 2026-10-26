using Microsoft.Extensions.Logging;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The deploy step that prepares the database, run once per release before the new tasks start.
/// For each module: make sure its login role and its schema exist, apply its migrations as the
/// owner, then let the module's role use its own schema, and nothing else.
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
            var module = databases.Single(d => d.Module == migrator.Module);
            await PrepareRoleAndSchema(ownerConnectionString, module, cancellationToken);
            await migrator.Migrate(ownerConnectionString, cancellationToken);
            await GrantTables(ownerConnectionString, module, cancellationToken);
            LogMigrated(logger, module.Module, module.Schema);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrated {Module} in schema {Schema}")]
    private static partial void LogMigrated(ILogger logger, string module, string schema);

    private static async Task PrepareRoleAndSchema(string owner, ModuleDatabase module, CancellationToken ct)
    {
        var role = Quote(module.Role);
        var schema = Quote(module.Schema);
        await Execute(owner, $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = {Literal(module.Role)}) THEN
                    CREATE ROLE {role} LOGIN PASSWORD {Literal(module.Password)};
                END IF;
            END $$;
            CREATE SCHEMA IF NOT EXISTS {schema};
            REVOKE ALL ON SCHEMA public FROM {role};
            GRANT USAGE ON SCHEMA {schema} TO {role};
            """, ct);
    }

    private static Task GrantTables(string owner, ModuleDatabase module, CancellationToken ct)
    {
        var role = Quote(module.Role);
        var schema = Quote(module.Schema);
        return Execute(owner, $"""
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO {role};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {role};
            """, ct);
    }

    private static async Task Execute(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
