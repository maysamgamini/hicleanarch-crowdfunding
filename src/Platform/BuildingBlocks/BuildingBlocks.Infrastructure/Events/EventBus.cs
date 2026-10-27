using System.Text.Json;
using System.Threading.Channels;

using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>A committed outbox row, as it travels inside the process.</summary>
/// <summary>
/// A committed outbox row, as it travels inside the process. <see cref="TraceParent"/> is the W3C id
/// of the trace that wrote it, so the handlers' work continues that trace, across modules.
/// </summary>
public sealed record OutboxRecord(string Module, Guid EventId, string Type, string Payload, string? TraceParent = null,
    IReadOnlyList<string>? Sinks = null, int Version = 1, DateTimeOffset OccurredAt = default);

/// <summary>The in-process transport: committed events wait here for the dispatcher.</summary>
public sealed class EventBus
{
    internal static readonly JsonSerializerOptions Json = ContractJson.Options;

    private readonly Channel<OutboxRecord> _channel = Channel.CreateUnbounded<OutboxRecord>();

    public void Publish(OutboxRecord record) => _channel.Writer.TryWrite(record);

    internal ChannelReader<OutboxRecord> Reader => _channel.Reader;

    internal static OutboxMessage ToMessage(IntegrationEvent integrationEvent) => new()
    {
        Id = integrationEvent.EventId,
        Type = integrationEvent.GetType().Name,
        Version = integrationEvent.Version,
        OccurredAt = integrationEvent.OccurredAt,
        Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), Json),
        TraceParent = System.Diagnostics.Activity.Current?.Id,
    };
}
