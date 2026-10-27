using BuildingBlocks.Infrastructure.Events;

using CrowdFunding.Messaging;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace IntegrationTests;

/// <summary>
/// A host stops running a module only when no outbox row here still owes that module an in-process
/// delivery: the module's new host reads its queue, never another module's outbox. The tests use a module
/// no host runs ("Ghost"), so their rows never stop another test's host.
/// </summary>
public sealed class OpenSinksTests(Database database)
{
    private const string Ghost = "Ghost";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A Campaigns outbox row with <paramref name="sinks"/>, delivered in process or not, and already published.</summary>
    private async Task<Guid> Row(string[] sinks, bool delivered)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO campaigns.outbox (id, type, version, occurred_at, payload, attempts, sinks, delivered_at, published_at)
            VALUES (@id, 'CampaignPublished', 1, now(), '{}'::jsonb, 0, @sinks, CASE WHEN @delivered THEN now() END, now())
            """, connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("sinks", sinks);
        insert.Parameters.AddWithValue("delivered", delivered);
        await insert.ExecuteNonQueryAsync(Ct);
        return id;
    }

    [Fact]
    public async Task A_host_that_no_longer_runs_a_module_refuses_to_start_while_a_row_owes_it_an_in_process_delivery()
    {
        await Row([Ghost], delivered: true); // delivered: owes nothing
        await Row([OutboxSink.Broker], delivered: false); // for the broker only: nothing in process
        var open = await Row([Ghost, OutboxSink.Broker], delivered: false);

        await using var host = database.StartHost(services => services.AddOpenSinksCheck(Ghost));
        var refused = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("This host no longer runs Ghost, but outbox rows still owe it an in-process delivery (1 in the Campaigns outbox)", refused.ToString(), StringComparison.Ordinal);

        await database.Execute($"UPDATE campaigns.outbox SET delivered_at = now() WHERE id = '{open}'");
        await using var again = database.StartHost(services => services.AddOpenSinksCheck(Ghost));
        _ = again.CreateClient(); // starts: nothing is owed any more
    }

    [Fact]
    public async Task The_open_sinks_command_reports_the_rows_and_fails_until_there_are_none()
    {
        await using var host = database.StartHost();
        var output = new StringWriter();

        Assert.Equal(2, await OpenSinks.Run(host.Services, [], output, Ct));
        Assert.Equal(0, await OpenSinks.Run(host.Services, ["--module", "Nobody"], output, Ct));
        Assert.Contains("No outbox row owes Nobody an in-process delivery.", output.ToString(), StringComparison.Ordinal);

        var open = await Row(["Nobody"], delivered: false);
        Assert.Equal(1, await OpenSinks.Run(host.Services, ["--module", "Nobody"], output, Ct));
        Assert.Contains("Outbox rows still owe Nobody an in-process delivery: 1 in the Campaigns outbox.", output.ToString(), StringComparison.Ordinal);
        await database.Execute($"UPDATE campaigns.outbox SET delivered_at = now() WHERE id = '{open}'");
    }
}
