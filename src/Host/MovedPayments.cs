using System.Globalization;

using Npgsql;

namespace CrowdFunding.Host;

/// <summary>
/// <c>drop-moved-payments</c> (CQA-2102): what s4-l07's move left of Payments on the shared cluster, its
/// old schema and its fenced login, leave it a week after the fence. Until s4-l10 the move task's
/// <c>drop-old</c> step did this; s4-l10 removed that task, and no other task with the shared owner's
/// rights runs Payments' image. So the monolith's migrate task, which runs as the shared cluster's owner,
/// runs this after <c>migrate</c> on every deploy (scripts/deploy-monolith.sh).
/// <para>
/// It drops only what the fence marked: a role that cannot log in and carries the fence's note. A role
/// that can log in is in use (on a laptop and in the tests, Payments shares the monolith's database), and
/// one the fence did not mark is not the move's: both are left, and so is a schema with no fenced role.
/// </para>
/// </summary>
public static class MovedPayments
{
    public const string Command = "drop-moved-payments";

    /// <summary>The note s4-l07's fence leaves on the role (MovePayments in Payments.Infrastructure).</summary>
    public const string FenceNote = "payments-move: fenced at ";

    /// <summary>How long the old copy stays after the fence, as s4-l07's drop-old waited.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>Drops <paramref name="schema"/> and <paramref name="role"/> if the fence marked the role at least <see cref="KeepFor"/> ago. Always 0: nothing here fails a deploy.</summary>
    public static async Task<int> Run(string owner, TextWriter output, DateTimeOffset now, CancellationToken ct,
        string role = "payments_app", string schema = "payments")
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync(ct);

        await using var read = new NpgsqlCommand("""
            SELECT r.rolcanlogin, shobj_description(r.oid, 'pg_authid')
            FROM pg_roles r WHERE r.rolname = @role
            """, connection);
        read.Parameters.AddWithValue("role", role);
        await using (var row = await read.ExecuteReaderAsync(ct))
        {
            if (!await row.ReadAsync(ct))
            {
                await output.WriteLineAsync($"No role {role} on this cluster: nothing of the move is left.");
                return 0;
            }

            var canLogIn = row.GetBoolean(0);
            var note = row.IsDBNull(1) ? null : row.GetString(1);
            if (canLogIn || note is null || !note.StartsWith(FenceNote, StringComparison.Ordinal))
            {
                await output.WriteLineAsync($"The role {role} is not one the move fenced: it is left as it is.");
                return 0;
            }

            var fencedAt = DateTimeOffset.Parse(note[FenceNote.Length..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            if (now < fencedAt + KeepFor)
            {
                await output.WriteLineAsync($"Payments' old copy is kept until {fencedAt + KeepFor:O}, a week after the fence.");
                return 0;
            }
        }

        var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(role);
        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schema);
        await using var drop = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE; DROP OWNED BY {quotedRole}; DROP ROLE {quotedRole};", connection);
        await drop.ExecuteNonQueryAsync(ct);
        await output.WriteLineAsync($"Dropped Payments' old schema {schema} and its fenced role {role} from the shared cluster.");
        return 0;
    }
}
