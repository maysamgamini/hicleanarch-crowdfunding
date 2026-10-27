using System.Text.Json;
using System.Threading.Channels;

using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>A committed outbox row, as it travels inside the process.</summary>
public sealed record OutboxRecord(string Module, Guid EventId, string Type, string Payload);

/// <summary>The in-process transport: committed events wait here for the dispatcher.</summary>
public sealed class EventBus
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
    };
}
