using System.Collections.Concurrent;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;

using Campaigns.Contracts;
using Campaigns.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace IntegrationTests;

/// <summary>When the database itself fails, the dispatcher loses nothing: the row waits for recovery.</summary>
public sealed class DispatcherFaultTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record DoomedPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    public sealed record LaterPing(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    private static readonly ConcurrentDictionary<Guid, int> Tries = new();

    private sealed class AlwaysFails : IIntegrationEventHandler<DoomedPing>
    {
        public Task Handle(DoomedPing e, CancellationToken ct)
        {
            Tries.AddOrUpdate(e.EventId, 1, (_, n) => n + 1);
            throw new InvalidOperationException("the handler fails");
        }
    }

    private sealed class Succeeds : IIntegrationEventHandler<LaterPing>
    {
        public Task Handle(LaterPing e, CancellationToken ct)
        {
            Tries.AddOrUpdate(e.EventId, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every module's events, except that recording a failure throws, as it would with the database down.</summary>
    private sealed class FailingRecord(IModuleEvents inner) : IModuleEvents
    {
        public string Module => inner.Module;

        public Task<IReadOnlyList<OutboxRecord>> Undelivered(CancellationToken ct) => inner.Undelivered(ct);

        public Task<TimeSpan?> OldestUndeliveredAge(CancellationToken ct) => inner.OldestUndeliveredAge(ct);

        public Task MarkDelivered(Guid eventId, CancellationToken ct) => inner.MarkDelivered(eventId, ct);

        public Task RecordFailure(Guid eventId, string error, CancellationToken ct) => throw new NpgsqlException("the database is down");

        public Task HandleOnce(string handler, Guid eventId, Func<IServiceProvider, CancellationToken, Task> handle, CancellationToken ct) =>
            inner.HandleOnce(handler, eventId, handle, ct);
    }

    private WebApplicationFactory<Program> Host() => database.StartHost(services =>
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IModuleEvents)).ToList())
        {
            services.Remove(descriptor);
            services.AddSingleton<IModuleEvents>(sp => new FailingRecord((IModuleEvents)descriptor.ImplementationFactory!(sp)));
        }

        services
            .AddEventHandler<DoomedPing, AlwaysFails>(CampaignsModuleName.Value)
            .AddEventHandler<LaterPing, Succeeds>(CampaignsModuleName.Value)
            .Configure<OutboxOptions>(o => o.RetryDelays = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)]);
    });

    private static async Task Write(WebApplicationFactory<Program> host, IntegrationEvent e)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetOutbox(CampaignsModuleName.Value).Add(e, Ct);
        await transaction.CommitAsync(Ct);
    }

    [Fact]
    public async Task A_failing_handler_whose_failure_cannot_be_recorded_leaves_the_row_for_recovery()
    {
        await using var host = Host();
        var doomed = new DoomedPing(Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Write(host, doomed);
        await Scenario.Eventually(() => Task.FromResult(Tries.GetValueOrDefault(doomed.EventId) == 3));

        // The dispatcher is still alive: a later event is delivered.
        var later = new LaterPing(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await Write(host, later);
        await Scenario.Eventually(() => Task.FromResult(Tries.ContainsKey(later.EventId)));

        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new NpgsqlCommand("SELECT delivered_at IS NULL FROM campaigns.outbox WHERE id = @id", owner);
        read.Parameters.AddWithValue("id", doomed.EventId);
        Assert.True((bool)(await read.ExecuteScalarAsync(Ct))!);
    }
}
