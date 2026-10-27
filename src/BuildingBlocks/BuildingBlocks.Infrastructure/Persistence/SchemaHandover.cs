using Npgsql;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Hands a module's schema to its service's own migration role, once, when the module leaves the
/// monolith. It runs as the cluster's owner. From then on, only the service's migrate step changes the
/// schema, logged in as that role.
/// <para>
/// Owning the schema is not enough. <c>ALTER TABLE</c> and <c>CREATE INDEX</c> need ownership of the
/// table itself, and every object the monolith's migrate step created belongs to the owner. So each
/// object in the schema is handed over one by one: the schema, its tables (partitions included), views,
/// standalone sequences, routines and types. The migration history is one of the tables. Sequences
/// that belong to a column, indexes, constraints and triggers follow their table. <c>REASSIGN OWNED</c>
/// is not used, because it works per database and would hand every module's objects to one role.
/// </para>
/// <para>
/// The role's password is sent as its SCRAM verifier (<see cref="ScramVerifier"/>), never in plain text.
/// Grants the owner gave stay in place and are shown as given by the new owner, so the module's
/// login keeps its rights. Running it again changes nothing.
/// </para>
/// </summary>
public static class SchemaHandover
{
    public static async Task Run(string ownerConnectionString, string schema, string migratorConnectionString, CancellationToken cancellationToken)
    {
        var migrator = new NpgsqlConnectionStringBuilder(migratorConnectionString);
        var role = Quote(migrator.Username ?? throw new InvalidOperationException("The migration role's connection string has no user name."));
        var roleName = Literal(migrator.Username);
        var schemaName = Literal(schema);

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($$"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = {{roleName}}) THEN
                    CREATE ROLE {{role}} LOGIN PASSWORD {{Literal(ScramVerifier.For(migrator.Password ?? ""))}};
                END IF;
            END $$;
            -- Giving an object away needs the right to become the new owner. Without superuser rights (as
            -- on Aurora), the owner created this role and may grant it to itself: SET only, so the owner
            -- does not quietly keep the rights of the schema's new owner.
            DO $$
            BEGIN
                IF NOT pg_has_role(current_user, {{roleName}}, 'SET') THEN
                    EXECUTE format('GRANT %I TO %I WITH INHERIT FALSE, SET TRUE', {{roleName}}, current_user);
                END IF;
            END $$;
            -- The new owner of each object needs CREATE on the schema; the owner needs USAGE on it to
            -- reach the objects, so the schema itself changes hands last.
            DO $$
            BEGIN
                IF (SELECT nspowner FROM pg_namespace WHERE nspname = {{schemaName}}) <> {{roleName}}::regrole THEN
                    EXECUTE format('GRANT USAGE, CREATE ON SCHEMA %I TO %I', {{schemaName}}, {{roleName}});
                END IF;
            END $$;
            DO $$
            DECLARE
                o record;
            BEGIN
                -- Tables, partitions, views and standalone sequences. A sequence that belongs to a
                -- column (serial, identity) moves with its table and may not be moved alone.
                FOR o IN
                    SELECT c.oid::regclass AS name,
                           CASE c.relkind WHEN 'S' THEN 'SEQUENCE' WHEN 'v' THEN 'VIEW'
                                          WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'f' THEN 'FOREIGN TABLE'
                                          ELSE 'TABLE' END AS kind
                    FROM pg_class c
                    WHERE c.relnamespace = {{schemaName}}::regnamespace
                      AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
                      AND c.relowner <> {{roleName}}::regrole
                      AND NOT (c.relkind = 'S' AND EXISTS (
                          SELECT FROM pg_depend d
                          WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype IN ('a', 'i')))
                LOOP
                    EXECUTE format('ALTER %s %s OWNER TO %I', o.kind, o.name, {{roleName}});
                END LOOP;

                -- Functions, procedures and aggregates.
                FOR o IN
                    SELECT p.oid::regprocedure AS name
                    FROM pg_proc p
                    WHERE p.pronamespace = {{schemaName}}::regnamespace AND p.proowner <> {{roleName}}::regrole
                LOOP
                    EXECUTE format('ALTER ROUTINE %s OWNER TO %I', o.name, {{roleName}});
                END LOOP;

                -- Enums, domains, ranges and standalone composite types. Array types and the row
                -- types of tables follow their owner.
                FOR o IN
                    SELECT t.oid::regtype AS name, CASE t.typtype WHEN 'd' THEN 'DOMAIN' ELSE 'TYPE' END AS kind
                    FROM pg_type t
                    LEFT JOIN pg_class c ON c.oid = t.typrelid
                    WHERE t.typnamespace = {{schemaName}}::regnamespace
                      AND t.typowner <> {{roleName}}::regrole
                      AND t.typtype IN ('e', 'd', 'r', 'c')
                      AND (t.typtype <> 'c' OR c.relkind = 'c')
                LOOP
                    EXECUTE format('ALTER %s %s OWNER TO %I', o.kind, o.name, {{roleName}});
                END LOOP;
            END $$;
            DO $$
            BEGIN
                IF (SELECT nspowner FROM pg_namespace WHERE nspname = {{schemaName}}) <> {{roleName}}::regrole THEN
                    EXECUTE format('ALTER SCHEMA %I OWNER TO %I', {{schemaName}}, {{roleName}});
                END IF;
            END $$;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Whether the schema already belongs to the migration role: then only the service migrates it.</summary>
    public static async Task<bool> IsHandedOver(string ownerConnectionString, string schema, string migratorConnectionString, CancellationToken cancellationToken)
    {
        var role = new NpgsqlConnectionStringBuilder(migratorConnectionString).Username;
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = @schema", connection);
        command.Parameters.AddWithValue("schema", schema);
        return await command.ExecuteScalarAsync(cancellationToken) is string owner && owner == role;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
