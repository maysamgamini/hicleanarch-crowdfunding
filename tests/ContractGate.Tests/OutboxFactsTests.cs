using Npgsql;

using Testcontainers.PostgreSql;

namespace ContractGate.Tests;

/// <summary>The producer's outbox, as can-retire reads it: when an event type was last written, and how many rows still owe a delivery.</summary>
public sealed class OutboxFactsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    [Fact]
    public async Task The_last_write_and_the_rows_still_owed_are_read_for_one_event_type()
    {
        await Insert("CampaignCreatorRecorded", "2026-10-01T10:00:00Z", delivered: true);
        await Insert("CampaignCreatorRecorded", "2026-10-03T10:00:00Z", delivered: false);
        await Insert("CampaignPublished", "2026-11-01T10:00:00Z", delivered: false);

        var facts = await Retirement.ReadOutbox(_container.GetConnectionString(), "Campaigns.CampaignCreatorRecorded", TestContext.Current.CancellationToken);

        Assert.Equal(new OutboxFacts(DateTimeOffset.Parse("2026-10-03T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 1), facts);
    }

    [Fact]
    public async Task A_row_is_owed_while_any_of_its_sinks_is_unmet()
    {
        // In process done, broker not yet: owed. Broker done, in process not yet: owed. Both done: not owed.
        await Insert("CampaignCreatorRecorded", "2026-10-01T10:00:00Z", delivered: true, sinks: ["Pledges", "broker"], published: false);
        await Insert("CampaignCreatorRecorded", "2026-10-02T10:00:00Z", delivered: false, sinks: ["Pledges", "broker"], published: true);
        await Insert("CampaignCreatorRecorded", "2026-10-03T10:00:00Z", delivered: true, sinks: ["Pledges", "broker"], published: true);
        await Insert("CampaignCreatorRecorded", "2026-10-04T10:00:00Z", delivered: false, sinks: ["broker"], published: true);

        var facts = await Retirement.ReadOutbox(_container.GetConnectionString(), "Campaigns.CampaignCreatorRecorded", TestContext.Current.CancellationToken);

        Assert.Equal(2, facts.Owed);
    }

    [Fact]
    public async Task An_event_type_never_written_has_no_last_write()
    {
        var facts = await Retirement.ReadOutbox(_container.GetConnectionString(), "Campaigns.CampaignCreatorRecorded", TestContext.Current.CancellationToken);

        Assert.Equal(new OutboxFacts(null, 0), facts);
    }

    [Fact]
    public async Task A_module_name_that_is_not_a_name_is_refused() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Retirement.ReadOutbox(_container.GetConnectionString(), "campaigns\".outbox; --.CampaignCreatorRecorded", TestContext.Current.CancellationToken));

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await Execute("""
            CREATE SCHEMA campaigns;
            CREATE TABLE campaigns.outbox (
                id uuid PRIMARY KEY, type varchar(200) NOT NULL, version integer NOT NULL, occurred_at timestamptz NOT NULL,
                payload jsonb NOT NULL, delivered_at timestamptz, attempts integer NOT NULL, last_error text, trace_parent varchar(60),
                sinks text[], published_at timestamptz);
            """);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private async Task Insert(string type, string occurredAt, bool delivered, string[]? sinks = null, bool published = false)
    {
        var at = DateTimeOffset.Parse(occurredAt, System.Globalization.CultureInfo.InvariantCulture);
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO campaigns.outbox (id, type, version, occurred_at, payload, delivered_at, attempts, sinks, published_at) VALUES (gen_random_uuid(), @type, 1, @at, '{}', @delivered, 0, @sinks, @published)",
            connection);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("delivered", delivered ? (object)at : DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("sinks", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)sinks ?? DBNull.Value });
        command.Parameters.AddWithValue("published", published ? (object)at : DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
