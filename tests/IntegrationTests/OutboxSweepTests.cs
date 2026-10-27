using System.Collections.Concurrent;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;

using Campaigns.Contracts;
using Campaigns.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Lesson s3-l16, built: the dispatcher survives a database that is down at start, sweeps what stayed
/// undelivered, delivers through a fixed number of workers, and finishes what it holds when it stops.
/// </summary>
public sealed class OutboxSweepTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ConcurrentDictionary<Guid, int> Handled = new();

    public sealed record StartPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record StubbornPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record BatchPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record SlowPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record BacklogPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record FreshPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    /// <summary>A backlog's handler: each takes a while.</summary>
    private sealed class Backlog : IIntegrationEventHandler<BacklogPing>
    {
        public async Task Handle(BacklogPing e, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            Count(e.EventId);
        }
    }

    private sealed class Fresh : IIntegrationEventHandler<FreshPing>
    {
        public Task Handle(FreshPing e, CancellationToken ct)
        {
            Count(e.EventId);
            return Task.CompletedTask;
        }
    }

    private static void Count(Guid id) => Handled.AddOrUpdate(id, 1, (_, n) => n + 1);

    private sealed class Counts : IIntegrationEventHandler<StartPing>
    {
        public Task Handle(StartPing e, CancellationToken ct)
        {
            Count(e.EventId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Fails five times, more than the dispatcher's own tries, then succeeds.</summary>
    private sealed class Stubborn : IIntegrationEventHandler<StubbornPing>
    {
        private static readonly ConcurrentDictionary<Guid, int> Tries = new();

        public Task Handle(StubbornPing e, CancellationToken ct)
        {
            if (Tries.AddOrUpdate(e.EventId, 1, (_, n) => n + 1) <= 5)
            {
                throw new InvalidOperationException("still failing");
            }

            Count(e.EventId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Takes a moment, and records how many deliveries ran at once.</summary>
    private sealed class Concurrency : IIntegrationEventHandler<BatchPing>
    {
        public static int Running;
        public static int Most;

        public async Task Handle(BatchPing e, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Running);
            InterlockedMax(ref Most, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref Running);
            Count(e.EventId);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
            {
                if (Interlocked.CompareExchange(ref target, value, seen) == seen)
                {
                    return;
                }
            }
        }
    }

    private sealed class Slow : IIntegrationEventHandler<SlowPing>
    {
        public static readonly ConcurrentDictionary<Guid, byte> Started = new();

        public async Task Handle(SlowPing e, CancellationToken ct)
        {
            Started.TryAdd(e.EventId, 0);
            await Task.Delay(500, ct);
            Count(e.EventId);
        }
    }

    /// <summary>Every module's outbox, unreachable until the test says the database is back.</summary>
    private sealed class DownAtFirst(IModuleEvents inner, Outage outage) : IModuleEvents
    {
        public string Module => inner.Module;

        public Task<IReadOnlyList<OutboxRecord>> Undelivered(DateTimeOffset? occurredBefore, int limit, CancellationToken ct) =>
            outage.Down ? throw new NpgsqlException("Failed to connect to 127.0.0.1:5432") : inner.Undelivered(occurredBefore, limit, ct);

        public Task<TimeSpan?> OldestUndeliveredAge(CancellationToken ct) =>
            outage.Down ? throw new NpgsqlException("Failed to connect to 127.0.0.1:5432") : inner.OldestUndeliveredAge(ct);

        public Task MarkDelivered(Guid eventId, CancellationToken ct) => inner.MarkDelivered(eventId, ct);

        public Task RecordFailure(Guid eventId, string error, CancellationToken ct) => inner.RecordFailure(eventId, error, ct);

        public Task HandleOnce(string handler, Guid eventId, Func<IServiceProvider, CancellationToken, Task> handle, CancellationToken ct) =>
            inner.HandleOnce(handler, eventId, handle, ct);
    }

    private sealed class Outage
    {
        public volatile bool Down = true;
    }

    private WebApplicationFactory<Program> Host(Action<OutboxOptions> outbox, Action<IServiceCollection>? more = null) => database.StartHost(services =>
    {
        services
            .AddEventHandler<StartPing, Counts>(CampaignsModuleName.Value)
            .AddEventHandler<StubbornPing, Stubborn>(CampaignsModuleName.Value)
            .AddEventHandler<BatchPing, Concurrency>(CampaignsModuleName.Value)
            .AddEventHandler<SlowPing, Slow>(CampaignsModuleName.Value)
            .Configure<OutboxOptions>(o =>
            {
                o.RetryDelays = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)];
                outbox(o);
            });
        more?.Invoke(services);
    });

    private async Task<bool> Delivered(Guid eventId)
    {
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new NpgsqlCommand("SELECT delivered_at IS NOT NULL FROM campaigns.outbox WHERE id = @id", owner);
        read.Parameters.AddWithValue("id", eventId);
        return await read.ExecuteScalarAsync(Ct) is true;
    }

    private static async Task Write(WebApplicationFactory<Program> host, params IntegrationEvent[] events)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetOutbox(CampaignsModuleName.Value).AddRange(events, Ct);
        await transaction.CommitAsync(Ct);
    }

    [Fact]
    public async Task A_task_that_starts_while_the_database_is_down_keeps_running_and_delivers_once_it_is_back()
    {
        // A row a stopped task left behind.
        var ping = new StartPing(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await using (var owner = new NpgsqlConnection(database.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO campaigns.outbox (id, type, version, occurred_at, payload, attempts) VALUES (@id, @type, 1, now(), @payload::jsonb, 0)", owner);
            insert.Parameters.AddWithValue("id", ping.EventId);
            insert.Parameters.AddWithValue("type", nameof(StartPing));
            insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(ping, JsonSerializerOptions.Web));
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var outage = new Outage();
        await using var host = Host(_ => { }, services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IModuleEvents)).ToList())
            {
                services.Remove(descriptor);
                services.AddSingleton<IModuleEvents>(sp => new DownAtFirst((IModuleEvents)descriptor.ImplementationFactory!(sp), outage));
            }
        });
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await Task.Delay(500, Ct); // several start-up sweeps fail
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.False(Handled.ContainsKey(ping.EventId));

        outage.Down = false;
        await Scenario.Eventually(() => Delivered(ping.EventId));
        Assert.Equal(1, Handled[ping.EventId]);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task An_event_still_failing_after_its_retries_is_delivered_by_a_sweep_without_a_restart()
    {
        await using var host = Host(o =>
        {
            o.SweepEvery = TimeSpan.FromMilliseconds(200);
            o.RedeliverAfter = TimeSpan.FromMilliseconds(300);
        });
        var ping = new StubbornPing(Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Write(host, ping);

        await Scenario.Eventually(() => Delivered(ping.EventId));
        Assert.Equal(1, Handled[ping.EventId]);
    }

    [Fact]
    public async Task A_commit_of_many_events_never_runs_more_deliveries_at_once_than_the_bound()
    {
        await using var host = Host(o => o.MaxConcurrentDeliveries = 3);
        var pings = Enumerable.Range(0, 60).Select(_ => new BatchPing(Guid.NewGuid(), DateTimeOffset.UtcNow)).ToArray();

        await Write(host, pings);

        await Scenario.Eventually(() => Task.FromResult(pings.All(p => Handled.ContainsKey(p.EventId))));
        Assert.InRange(Concurrency.Most, 1, 3);
    }

    [Fact]
    public async Task A_backlog_found_at_start_does_not_hold_back_a_fresh_event()
    {
        // 400 rows left undelivered by an outage, each taking 100 ms to handle: 10 s of work for four workers.
        var backlog = Enumerable.Range(0, 400).Select(_ => new BacklogPing(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-10))).ToList();
        await using (var owner = new NpgsqlConnection(database.Owner))
        {
            await owner.OpenAsync(Ct);
            foreach (var ping in backlog)
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO campaigns.outbox (id, type, version, occurred_at, payload, attempts) VALUES (@id, @type, 1, @at, @payload::jsonb, 0)", owner);
                insert.Parameters.AddWithValue("id", ping.EventId);
                insert.Parameters.AddWithValue("type", nameof(BacklogPing));
                insert.Parameters.AddWithValue("at", ping.OccurredAt);
                insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(ping, JsonSerializerOptions.Web));
                await insert.ExecuteNonQueryAsync(Ct);
            }
        }

        // Only this host knows the backlog's type, so no other test's host works through it.
        await using var host = Host(_ => { }, services => services
            .AddEventHandler<BacklogPing, Backlog>(CampaignsModuleName.Value)
            .AddEventHandler<FreshPing, Fresh>(CampaignsModuleName.Value));
        _ = host.Services; // start it: its first sweep finds the backlog
        await Scenario.Eventually(() => Task.FromResult(backlog.Any(p => Handled.ContainsKey(p.EventId)))); // the start-up sweep has begun
        var fresh = new FreshPing(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await Write(host, fresh);

        await Scenario.Eventually(() => Task.FromResult(Handled.ContainsKey(fresh.EventId)));
        Assert.True(backlog.Count(p => Handled.ContainsKey(p.EventId)) < backlog.Count / 2); // it did not wait for the backlog
    }

    [Fact]
    public async Task Stopping_finishes_the_delivery_in_hand()
    {
        var host = Host(_ => { });
        var ping = new SlowPing(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await Write(host, ping);
        await Scenario.Eventually(() => Task.FromResult(Slow.Started.ContainsKey(ping.EventId)));

        await host.DisposeAsync();

        Assert.Equal(1, Handled.GetValueOrDefault(ping.EventId));
        Assert.True(await Delivered(ping.EventId));
    }
}
