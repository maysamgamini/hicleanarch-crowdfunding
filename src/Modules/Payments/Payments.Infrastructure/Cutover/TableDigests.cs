using Npgsql;

namespace Payments.Infrastructure.Cutover;

/// <summary>One table compared on both sides: its rows and whether every chunk's digest agrees.</summary>
internal sealed record TableComparison(string Table, bool OnBothSides, long SourceRows, long TargetRows, int Chunks, int? FirstDifferingChunk)
{
    public bool Matches => OnBothSides && FirstDifferingChunk is null;
}

/// <summary>
/// Compares a schema's content on two databases, table by table: a digest of the rows in primary-key
/// order, <c>md5(string_agg(t::text, '' ORDER BY key))</c>, in chunks of key ranges so a large table
/// is never one aggregate. A count would miss a status changed on one side; the digest does not.
/// </summary>
internal static class TableDigests
{
    public const int DefaultChunkRows = 50_000;

    public static async Task<IReadOnlyList<TableComparison>> Compare(
        NpgsqlConnection source, NpgsqlConnection target, string schema, int chunkRows, CancellationToken ct)
    {
        await using var sourceSnapshot = await Snapshot(source, ct);
        await using var targetSnapshot = await Snapshot(target, ct);

        var sourceTables = await Tables(source, schema, ct);
        var targetTables = await Tables(target, schema, ct);
        var comparisons = new List<TableComparison>();
        foreach (var table in sourceTables.Union(targetTables, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!sourceTables.Contains(table) || !targetTables.Contains(table))
            {
                comparisons.Add(new TableComparison(table, false, 0, 0, 0, null));
                continue;
            }

            comparisons.Add(await CompareTable(source, target, schema, table, chunkRows, ct));
        }

        await sourceSnapshot.CommitAsync(ct);
        await targetSnapshot.CommitAsync(ct);
        return comparisons;
    }

    // One snapshot per side for every chunk, and the same text for the same value on both sides:
    // t::text depends on the session's time zone, date and interval styles and float digits.
    private static async Task<NpgsqlTransaction> Snapshot(NpgsqlConnection connection, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var settings = new NpgsqlCommand("""
            SET TRANSACTION READ ONLY;
            SET LOCAL TimeZone = 'UTC';
            SET LOCAL DateStyle = 'ISO, YMD';
            SET LOCAL IntervalStyle = 'postgres';
            SET LOCAL extra_float_digits = 1;
            SET LOCAL bytea_output = 'hex';
            """, connection, transaction);
        await settings.ExecuteNonQueryAsync(ct);
        return transaction;
    }

    private static async Task<HashSet<string>> Tables(NpgsqlConnection connection, string schema, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relkind IN ('r', 'p') AND NOT c.relispartition
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<TableComparison> CompareTable(
        NpgsqlConnection source, NpgsqlConnection target, string schema, string table, int chunkRows, CancellationToken ct)
    {
        var name = $"{Sql.Quote(schema)}.{Sql.Quote(table)}";
        var key = await KeyColumns(source, name, ct);
        if (key.Count == 0)
        {
            // No primary key: the whole table in the order of its rows' text, as one chunk.
            var (sourceRows, sourceDigest) = await Digest(source, name, "t::text", "", [], ct);
            var (targetRows, targetDigest) = await Digest(target, name, "t::text", "", [], ct);
            return new TableComparison(table, true, sourceRows, targetRows, 1, sourceDigest == targetDigest && sourceRows == targetRows ? null : 0);
        }

        var order = string.Join(", ", key.Select(k => $"t.{Sql.Quote(k.Name)}"));
        var bounds = await ChunkBounds(source, name, key, order, chunkRows, ct);
        long totalSource = 0, totalTarget = 0;
        int? differing = null;
        for (var i = 0; i <= bounds.Count; i++)
        {
            var (where, parameters) = Range(key, i == 0 ? null : bounds[i - 1], i == bounds.Count ? null : bounds[i]);
            var (sourceRows, sourceDigest) = await Digest(source, name, order, where, parameters, ct);
            var (targetRows, targetDigest) = await Digest(target, name, order, where, parameters, ct);
            totalSource += sourceRows;
            totalTarget += targetRows;
            if (differing is null && (sourceRows != targetRows || sourceDigest != targetDigest))
            {
                differing = i;
            }
        }

        return new TableComparison(table, true, totalSource, totalTarget, bounds.Count + 1, differing);
    }

    private sealed record KeyColumn(string Name, string Type);

    private static async Task<IReadOnlyList<KeyColumn>> KeyColumns(NpgsqlConnection connection, string table, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.attname, format_type(a.atttypid, a.atttypmod)
            FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = @table::regclass AND i.indisprimary
            ORDER BY array_position(i.indkey::int2[], a.attnum)
            """, connection);
        command.Parameters.AddWithValue("table", table);
        var columns = new List<KeyColumn>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new KeyColumn(reader.GetString(0), reader.GetString(1)));
        }

        return columns;
    }

    // The key of every chunkRows-th row on the source, as text: the upper bound of each full chunk.
    // The last chunk has no upper bound, so rows only the target has are counted too.
    private static async Task<IReadOnlyList<string[]>> ChunkBounds(
        NpgsqlConnection source, string table, IReadOnlyList<KeyColumn> key, string order, int chunkRows, CancellationToken ct)
    {
        var columns = string.Join(", ", key.Select((k, i) => $"t.{Sql.Quote(k.Name)}::text AS k{i}"));
        await using var command = new NpgsqlCommand($"""
            SELECT {string.Join(", ", key.Select((_, i) => $"k{i}"))} FROM (
                SELECT {columns}, row_number() OVER (ORDER BY {order}) AS n FROM {table} t
            ) numbered
            WHERE n % @rows = 0 ORDER BY n
            """, source);
        command.Parameters.AddWithValue("rows", (long)chunkRows);
        var bounds = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            bounds.Add([.. Enumerable.Range(0, key.Count).Select(reader.GetString)]);
        }

        return bounds;
    }

    private static (string Where, IReadOnlyList<NpgsqlParameter> Parameters) Range(IReadOnlyList<KeyColumn> key, string[]? after, string[]? upTo)
    {
        var row = $"ROW({string.Join(", ", key.Select(k => $"t.{Sql.Quote(k.Name)}"))})";
        var conditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        if (after is not null)
        {
            conditions.Add($"{row} > ROW({string.Join(", ", key.Select((k, i) => $"@lo{i}::{k.Type}"))})");
            parameters.AddRange(after.Select((v, i) => new NpgsqlParameter($"lo{i}", v)));
        }

        if (upTo is not null)
        {
            conditions.Add($"{row} <= ROW({string.Join(", ", key.Select((k, i) => $"@hi{i}::{k.Type}"))})");
            parameters.AddRange(upTo.Select((v, i) => new NpgsqlParameter($"hi{i}", v)));
        }

        return (conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions), parameters);
    }

    private static async Task<(long Rows, string Digest)> Digest(
        NpgsqlConnection connection, string table, string order, string where, IReadOnlyList<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*), coalesce(md5(string_agg(t::text, '' ORDER BY {order})), '') FROM {table} t {where}", connection);
        foreach (var p in parameters)
        {
            command.Parameters.Add(p.Clone());
        }

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetString(1));
    }
}

internal static class Sql
{
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
