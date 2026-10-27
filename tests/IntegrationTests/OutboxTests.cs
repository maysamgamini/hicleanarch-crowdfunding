using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Identity;

using Campaigns.Contracts;
using Campaigns.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace IntegrationTests;

/// <summary>Events in process: written with the change, sent after the commit, handled once, recovered at start.</summary>
public sealed class OutboxTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Counts how often each event reached a handler's own code, past the inbox.</summary>
    private static readonly ConcurrentDictionary<Guid, int> Handled = new();

    public sealed record Ping(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record RecoveredPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record FlakyPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    private sealed class Counter : IIntegrationEventHandler<CampaignPublished>, IIntegrationEventHandler<Ping>, IIntegrationEventHandler<RecoveredPing>
    {
        public Task Handle(CampaignPublished e, CancellationToken ct) => Count(e.CampaignId);

        public Task Handle(Ping e, CancellationToken ct) => Count(e.EventId);

        public Task Handle(RecoveredPing e, CancellationToken ct) => Count(e.EventId);

        private static Task Count(Guid id)
        {
            Handled.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }
    }

    /// <summary>Fails twice for each event, then succeeds.</summary>
    private sealed class Flaky : IIntegrationEventHandler<FlakyPing>
    {
        private static readonly ConcurrentDictionary<Guid, int> Tries = new();

        public Task Handle(FlakyPing e, CancellationToken ct)
        {
            if (Tries.AddOrUpdate(e.EventId, 1, (_, n) => n + 1) <= 2)
            {
                throw new InvalidOperationException("not yet");
            }

            Handled.AddOrUpdate(e.EventId, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }
    }

    private WebApplicationFactory<Program> Host() => database.StartHost(services => services
        .AddEventHandler<CampaignPublished, Counter>(CampaignsModuleName.Value)
        .AddEventHandler<Ping, Counter>(CampaignsModuleName.Value)
            .AddEventSubscription<Ping>(CampaignsModuleName.Value, CampaignsModuleName.Value)
        .AddEventHandler<RecoveredPing, Counter>(CampaignsModuleName.Value)
            .AddEventSubscription<RecoveredPing>(CampaignsModuleName.Value, CampaignsModuleName.Value)
        .AddEventHandler<FlakyPing, Flaky>(CampaignsModuleName.Value)
            .AddEventSubscription<FlakyPing>(CampaignsModuleName.Value, CampaignsModuleName.Value)
        .Configure<OutboxOptions>(o => o.RetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)]));

    private static Task Eventually(Func<Task<bool>> condition) => Scenario.Eventually(condition);

    private async Task<DateTimeOffset?> DeliveredAt(Guid eventId)
    {
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new NpgsqlCommand("SELECT delivered_at FROM campaigns.outbox WHERE id = @id", owner);
        read.Parameters.AddWithValue("id", eventId);
        return await read.ExecuteScalarAsync(Ct) switch { DateTime d => new DateTimeOffset(d, TimeSpan.Zero), _ => null };
    }

    private async Task<bool> Exists(Guid eventId)
    {
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new NpgsqlCommand("SELECT count(*) FROM campaigns.outbox WHERE id = @id", owner);
        read.Parameters.AddWithValue("id", eventId);
        return (long)(await read.ExecuteScalarAsync(Ct))! == 1;
    }

    /// <summary>Writes an event the way a use case does: in a transaction of the module, then commit or roll back.</summary>
    private static async Task Write(WebApplicationFactory<Program> host, IntegrationEvent e, bool commit)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetOutbox(CampaignsModuleName.Value).Add(e, Ct);
        if (commit)
        {
            await transaction.CommitAsync(Ct);
        }
        else
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    [Fact]
    public async Task Publishing_a_campaign_reaches_a_handler()
    {
        await using var host = Host();
        var creator = Database.Client(host, Database.Token(Roles.Creator));
        var created = await creator.PostAsJsonAsync("/v1/campaigns", new
        {
            title = "Harbor Lights",
            goal_minor = 5_000_000,
            currency = "USD",
            deadline = "2030-01-01T00:00:00Z",
            tiers = new[] { new { title = "Early Bird", min_pledge_minor = 4_900, limit = 500 } },
        }, Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("campaign_id").GetString()!;

        (await creator.PostAsync($"/v1/campaigns/{id}/publish", null, Ct)).EnsureSuccessStatusCode();

        var campaignId = Guid.ParseExact(id["cmp_".Length..], "N");
        await Eventually(() => Task.FromResult(Handled.ContainsKey(campaignId)));
    }

    [Fact]
    public async Task An_event_rolled_back_is_neither_stored_nor_sent()
    {
        await using var host = Host();
        var ping = new Ping(Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Write(host, ping, commit: false);
        await Task.Delay(300, Ct);

        Assert.False(await Exists(ping.EventId));
        Assert.False(Handled.ContainsKey(ping.EventId));
    }

    [Fact]
    public async Task An_event_reaches_no_handler_before_its_transaction_commits()
    {
        await using var host = Host();
        var ping = new Ping(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetOutbox(CampaignsModuleName.Value).Add(ping, Ct);

        await Task.Delay(300, Ct);
        Assert.False(Handled.ContainsKey(ping.EventId));

        await transaction.CommitAsync(Ct);
        await Eventually(() => Task.FromResult(Handled.ContainsKey(ping.EventId)));
    }

    [Fact]
    public async Task A_committed_event_is_handled_and_its_row_marked_delivered()
    {
        await using var host = Host();
        var ping = new Ping(Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Write(host, ping, commit: true);

        await Eventually(async () => await DeliveredAt(ping.EventId) is not null);
        Assert.Equal(1, Handled[ping.EventId]);
    }

    [Fact]
    public async Task An_event_delivered_twice_is_handled_once()
    {
        await using var host = Host();
        var ping = new Ping(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await Write(host, ping, commit: true);
        await Eventually(async () => await DeliveredAt(ping.EventId) is not null);

        var record = new OutboxRecord(CampaignsModuleName.Value, ping.EventId, nameof(Ping), JsonSerializer.Serialize(ping, JsonSerializerOptions.Web));
        await host.Services.GetRequiredService<OutboxDispatcher>().Deliver(record, Ct);

        Assert.Equal(1, Handled[ping.EventId]);
    }

    [Fact]
    public async Task A_failing_handler_is_retried_until_it_succeeds()
    {
        await using var host = Host();
        var ping = new FlakyPing(Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Write(host, ping, commit: true);

        await Eventually(async () => await DeliveredAt(ping.EventId) is not null);
        Assert.Equal(1, Handled[ping.EventId]);
        Assert.Equal(2L, await Attempts(ping.EventId)); // each of the two failures was counted
    }

    private async Task<long> Attempts(Guid eventId)
    {
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new NpgsqlCommand("SELECT attempts FROM campaigns.outbox WHERE id = @id", owner);
        read.Parameters.AddWithValue("id", eventId);
        return Convert.ToInt64(await read.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task An_undelivered_row_is_sent_when_a_program_starts()
    {
        // A row left behind by a program that stopped right after its commit.
        var ping = new RecoveredPing(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await using (var owner = new NpgsqlConnection(database.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO campaigns.outbox (id, type, version, occurred_at, payload, attempts) VALUES (@id, @type, 1, now(), @payload::jsonb, 0)", owner);
            insert.Parameters.AddWithValue("id", ping.EventId);
            insert.Parameters.AddWithValue("type", nameof(RecoveredPing));
            insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(ping, JsonSerializerOptions.Web));
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using var host = Host();
        _ = host.Services; // starting the program is what recovers

        await Eventually(async () => await DeliveredAt(ping.EventId) is not null);
        Assert.Equal(1, Handled[ping.EventId]);
    }

    [Fact]
    public async Task An_event_cannot_be_written_outside_a_transaction()
    {
        await using var host = Host();
        await using var scope = host.Services.CreateAsyncScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetOutbox(CampaignsModuleName.Value).Add(new Ping(Guid.NewGuid(), DateTimeOffset.UtcNow), Ct));
    }
}
