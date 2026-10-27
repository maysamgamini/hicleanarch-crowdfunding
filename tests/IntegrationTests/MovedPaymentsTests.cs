using System.Globalization;

using CrowdFunding.Host;

using Npgsql;

namespace IntegrationTests;

/// <summary>
/// CQA-2102: the monolith's drop-moved-payments removes what s4-l07's move left of Payments on the shared
/// cluster, a week after the fence, and nothing else. Each test makes a schema and role of its own, as the
/// fence leaves them; Payments' real role and schema in this database are in use and must stay.
/// </summary>
public sealed class MovedPaymentsTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A schema with a table, and a role that owns something in it, fenced at <paramref name="fencedAt"/> (or not fenced).</summary>
    private async Task<(string Role, string Schema)> Left(DateTimeOffset? fencedAt, bool canLogIn = false)
    {
        var name = $"moved_{Guid.NewGuid():N}"[..20];
        var (role, schema) = ($"{name}_app", name);
        var note = fencedAt is { } at ? $"COMMENT ON ROLE {role} IS '{MovedPayments.FenceNote}{at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}';" : "";
        await database.Execute($"""
            CREATE ROLE {role} {(canLogIn ? "LOGIN" : "NOLOGIN")};
            CREATE SCHEMA {schema};
            CREATE TABLE {schema}.charges (id int);
            CREATE TABLE public.{name}_owned (id int);
            ALTER TABLE public.{name}_owned OWNER TO {role};
            {note}
            """);
        return (role, schema);
    }

    private async Task<(bool Role, bool Schema)> Exists((string Role, string Schema) left)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = @role), EXISTS (SELECT FROM pg_namespace WHERE nspname = @schema)", connection);
        command.Parameters.AddWithValue("role", left.Role);
        command.Parameters.AddWithValue("schema", left.Schema);
        await using var row = await command.ExecuteReaderAsync(Ct);
        await row.ReadAsync(Ct);
        return (row.GetBoolean(0), row.GetBoolean(1));
    }

    private Task<int> Run((string Role, string Schema) left, TextWriter output) =>
        MovedPayments.Run(database.Owner, output, Now, Ct, left.Role, left.Schema);

    [Fact]
    public async Task A_week_after_the_fence_the_old_schema_and_the_fenced_role_leave()
    {
        var left = await Left(Now - TimeSpan.FromDays(8));
        var output = new StringWriter();

        Assert.Equal(0, await Run(left, output));

        Assert.Equal((false, false), await Exists(left));
        Assert.Contains("Dropped Payments' old schema", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await Run(left, new StringWriter())); // again: nothing left, nothing to do
    }

    [Fact]
    public async Task Within_the_week_the_old_copy_is_kept()
    {
        var left = await Left(Now - TimeSpan.FromDays(2));
        var output = new StringWriter();

        Assert.Equal(0, await Run(left, output));

        Assert.Equal((true, true), await Exists(left));
        Assert.Contains("kept until", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)] // a role that can log in is in use, as Payments' is on a laptop
    [InlineData(false)] // a role the fence did not mark is not the move's
    public async Task A_role_the_move_did_not_fence_is_left_with_its_schema(bool canLogIn)
    {
        var left = await Left(fencedAt: canLogIn ? Now - TimeSpan.FromDays(30) : null, canLogIn);

        Assert.Equal(0, await Run(left, new StringWriter()));

        Assert.Equal((true, true), await Exists(left));
    }
}
