using Npgsql;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// A module's place in the shared database: its schema, and the role its connection string logs in as.
/// A module may log in as more than one role on its schema (for example, its background jobs as a
/// role without the request path's limits); each gets its own entry with its own <see cref="Module"/> name.
/// </summary>
public sealed record ModuleDatabase(string Module, string Schema, string ConnectionString)
{
    /// <summary>Settings the owner sets on this role, as <c>name = value</c> (for example <c>transaction_timeout = '2s'</c>).</summary>
    public IReadOnlyList<string> RoleSettings { get; init; } = [];

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

    /// <summary>
    /// SQL the owner runs after the module's role has been granted its tables, to take rights back
    /// where a rule needs it (for example, a ledger the role may only add to). Empty by default.
    /// </summary>
    string AfterGrants(string quotedRole) => "";

    /// <summary>
    /// Checks, once the roles are set up, that the database really enforces what the module relies
    /// on, as the roles that will use it. A failure fails the deploy step. Nothing by default.
    /// </summary>
    Task Verify(IReadOnlyList<ModuleDatabase> roles, CancellationToken cancellationToken) => Task.CompletedTask;
}
