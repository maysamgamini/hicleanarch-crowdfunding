using Npgsql;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>A module's place in the shared database: its schema, and the role its connection string logs in as.</summary>
public sealed record ModuleDatabase(string Module, string Schema, string ConnectionString)
{
    public string Role => new NpgsqlConnectionStringBuilder(ConnectionString).Username
        ?? throw new InvalidOperationException($"The {Module} connection string has no user name.");

    public string Password => new NpgsqlConnectionStringBuilder(ConnectionString).Password
        ?? throw new InvalidOperationException($"The {Module} connection string has no password.");
}

/// <summary>Applies one module's migrations, as the schema owner.</summary>
public interface IModuleMigrator
{
    string Module { get; }

    string Schema { get; }

    Task Migrate(string ownerConnectionString, CancellationToken cancellationToken);
}
