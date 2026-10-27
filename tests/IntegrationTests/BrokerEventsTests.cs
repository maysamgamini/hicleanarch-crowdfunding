using System.Text.Json;

using Amazon.SQS.Model;

using BuildingBlocks.Infrastructure.Events;

using Campaigns.Contracts;

using CrowdFunding.Messaging;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Lesson s4-l02: events leave the process, through SNS and SQS (Moto). Since s4-l10 every host here is a
/// monolith with its Payments service beside it, so what crosses is what crosses in AWS. The s4-l02 tests
/// of switching Payments' routes between InProcess and Broker went with the InProcess mode.
/// </summary>
public sealed class BrokerEventsTests(Database database) : IDisposable
{
    private const string RecordPayee = "Payments.RecordPayee";

    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private WebApplicationFactory<Program> Host(IReadOnlyDictionary<string, string?> broker, string? recoverAfter = null)
    {
        var host = database.StartHost(broker, recoverAfter: recoverAfter);
        _hosts.Add(host);
        _ = host.Services; // started
        return host;
    }

    private static CampaignPublished Published(Guid campaignId) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, campaignId, Guid.NewGuid(), 100_000, "USD", DateTimeOffset.UtcNow.AddDays(30), []);

    private static string Json(CampaignPublished published) => JsonSerializer.Serialize(published, JsonSerializerOptions.Web);

    /// <summary>A message as SNS delivers it with raw delivery: the event's body and its attributes.</summary>
    private static SendMessageRequest Message(string queueUrl, string body, string type = nameof(CampaignPublished), string producer = "Campaigns") => new()
    {
        QueueUrl = queueUrl,
        MessageBody = body,
        MessageAttributes = new()
        {
            [MessageAttributes.EventType] = new() { DataType = "String", StringValue = type },
            [MessageAttributes.EventVersion] = new() { DataType = "String", StringValue = "1" },
            [MessageAttributes.Producer] = new() { DataType = "String", StringValue = producer },
        },
    };

    private async Task<long> Scalar(string sql, object value)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<long> Payees(Guid campaignId) => Scalar("SELECT count(*) FROM payments.campaign_payees WHERE campaign_id = @value", campaignId);

    private Task<long> Inbox(Guid eventId) => Scalar($"SELECT count(*) FROM payments.inbox WHERE handler = '{RecordPayee}' AND event_id = @value", eventId);

    /// <summary>Writes a CampaignPublished row as the Campaigns outbox would have, with the given sinks and age.</summary>
    private async Task WriteRow(CampaignPublished published, string[]? sinks, TimeSpan age, bool publishedAlready = false)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO campaigns.outbox (id, type, version, occurred_at, payload, attempts, sinks, published_at, delivered_at)
            VALUES (@id, 'CampaignPublished', 1, now() - @age, @payload::jsonb, 0, @sinks, CASE WHEN @published THEN now() END, now())
            """, connection);
        insert.Parameters.AddWithValue("id", published.EventId);
        insert.Parameters.AddWithValue("age", age);
        insert.Parameters.AddWithValue("payload", Json(published));
        insert.Parameters.Add(new NpgsqlParameter("sinks", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)sinks ?? DBNull.Value });
        insert.Parameters.AddWithValue("published", publishedAlready);
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private static async Task Within(TimeSpan limit, Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until && !await condition())
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(await condition());
    }

    [Fact]
    public async Task A_new_row_stores_what_it_owes_when_it_is_written()
    {
        var broker = Host(database.Provision().Settings);

        var viaBroker = await new Scenario(database, broker).Publish(1_000);

        async Task<string> SinksOf(Guid campaignId)
        {
            await using var connection = new NpgsqlConnection(database.Owner);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SELECT array_to_string(sinks, ',') FROM campaigns.outbox WHERE type = 'CampaignPublished' AND payload->>'campaignId' = @id", connection);
            command.Parameters.AddWithValue("id", campaignId.ToString());
            return (string)(await command.ExecuteScalarAsync(Ct))!;
        }

        Assert.Equal("Campaigns,Pledges,Notifications,broker", await SinksOf(viaBroker.CampaignId));
    }

    [Fact]
    public async Task An_event_published_by_one_side_is_handled_once_by_the_other_even_when_delivered_twice()
    {
        var provisioned = database.Provision();
        Host(provisioned.Settings);
        using var sqs = database.Broker.Sqs();
        var queue = provisioned.Settings["Events:Broker:Queues:payments:Url"]!;
        var published = Published(Guid.NewGuid());

        await sqs.SendMessageAsync(Message(queue, Json(published)), Ct);
        await sqs.SendMessageAsync(Message(queue, Json(published)), Ct); // SNS delivers at least once

        await Within(TimeSpan.FromSeconds(10), async () => await Inbox(published.EventId) == 1);
        await Within(TimeSpan.FromSeconds(10), async () =>
            (await sqs.GetQueueAttributesAsync(queue, ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"], Ct))
                .Attributes.Values.All(v => v == "0"));
        Assert.Equal(1, await Payees(published.CampaignId));
        Assert.Equal(1, await Inbox(published.EventId));
    }

    [Fact]
    public async Task An_event_missing_a_required_field_goes_to_the_dead_letter_queue_unhandled()
    {
        var provisioned = database.Provision();
        Host(provisioned.Settings);
        using var sqs = database.Broker.Sqs();
        var published = Published(Guid.NewGuid());
        using var body = JsonDocument.Parse(Json(published));
        var withoutCreator = JsonSerializer.Serialize(body.RootElement.EnumerateObject().Where(p => p.Name != "creatorId")
            .ToDictionary(p => p.Name, p => p.Value));

        await sqs.SendMessageAsync(Message(provisioned.Settings["Events:Broker:Queues:payments:Url"]!, withoutCreator), Ct);

        // Five receives, a second apart, then SQS moves it.
        var deadLetters = provisioned.DeadLetterQueues["payments"];
        await Within(TimeSpan.FromSeconds(30), async () =>
            (await sqs.GetQueueAttributesAsync(deadLetters, ["ApproximateNumberOfMessages"], Ct)).ApproximateNumberOfMessages == 1);
        Assert.Equal(0, await Payees(published.CampaignId)); // not handled with an empty creator
    }

    [Fact]
    public async Task Replay_events_publishes_a_range_again_and_the_inbox_makes_it_harmless()
    {
        var host = Host(database.Provision().Settings);
        var campaign = await new Scenario(database, host).Publish(1_000);
        await Within(TimeSpan.FromSeconds(10), async () => await Payees(campaign.CampaignId) == 1);
        var from = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var to = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var output = new StringWriter();

        var exit = await EventReplay.Run(host.Services, ["--module", "Campaigns", "--type", "CampaignPublished", "--from", from, "--to", to], output, Ct);

        Assert.Equal(0, exit);
        var replayed = int.Parse(output.ToString().Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(replayed >= 1, output.ToString());
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // the reader has the copies by now
        Assert.Equal(1, await Payees(campaign.CampaignId));
        Assert.Equal(1, await Scalar(
            $"SELECT count(*) FROM payments.inbox i JOIN campaigns.outbox o ON o.id = i.event_id WHERE i.handler = '{RecordPayee}' AND o.payload->>'campaignId' = @value",
            campaign.CampaignId.ToString()));
    }

    [Fact]
    public async Task Two_tasks_recovering_at_once_claim_each_unpublished_row_once()
    {
        var events = Enumerable.Range(0, 200).Select(_ => Published(Guid.NewGuid())).ToList();
        foreach (var published in events)
        {
            await WriteRow(published, ["broker"], TimeSpan.Zero); // young: other tests' tasks recover only rows over 30 s old
        }

        // Two programs whose recovery passes take only rows over ten minutes old: only these claims take the young rows.
        var a = Host(database.Provision().Settings).Services.GetServices<IModuleEvents>().Single(m => m.Module == "Campaigns");
        var b = Host(database.Provision().Settings).Services.GetServices<IModuleEvents>().Single(m => m.Module == "Campaigns");
        var ids = events.Select(e => e.EventId).ToHashSet();

        async Task<List<Guid>> ClaimAll(IModuleEvents module)
        {
            var mine = new List<Guid>();
            IReadOnlyList<OutboxRecord> claimed;
            do
            {
                claimed = await module.ClaimUnpublished(TimeSpan.Zero, 25, TimeSpan.FromMinutes(2), Ct);
                mine.AddRange(claimed.Select(r => r.EventId).Where(ids.Contains));
            }
            while (claimed.Count > 0);

            return mine;
        }

        var claims = await Task.WhenAll(Task.Run(() => ClaimAll(a), Ct), Task.Run(() => ClaimAll(b), Ct));

        Assert.Equal(200, claims[0].Count + claims[1].Count);
        Assert.Equal(ids.Order(), claims.SelectMany(c => c).Order());
    }

    [Theory]
    [InlineData(OutboxModel.Undelivered, "outbox_undelivered")]
    [InlineData(OutboxModel.Unpublished, "outbox_unpublished")]
    public async Task The_outbox_queries_use_their_partial_index(string predicate, string index)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        // The table is small in a test, so a scan would win on cost; switched off, the planner shows whether the index can serve.
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        foreach (var query in new[]
                 {
                     $"SELECT min(occurred_at) FROM campaigns.outbox WHERE {predicate}",
                     $"SELECT id FROM campaigns.outbox WHERE {predicate} AND occurred_at < now() ORDER BY occurred_at LIMIT 10 FOR UPDATE SKIP LOCKED",
                 })
        {
            await using var explain = new NpgsqlCommand("EXPLAIN " + query, connection);
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    plan.Add(reader.GetString(0));
                }
            }

            Assert.Contains(plan, line => line.Contains(index, StringComparison.Ordinal));
        }
    }
}
