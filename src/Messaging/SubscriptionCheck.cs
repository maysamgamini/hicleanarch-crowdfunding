using System.Diagnostics.Metrics;
using System.Text.Json;

using Amazon.SimpleNotificationService;

using BuildingBlocks.Infrastructure.Events;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrowdFunding.Messaging;

/// <summary>
/// SNS accepts a publish to a topic with no matching subscription and drops it, so every <c>Broker</c>
/// route of this host is checked against the topics: at start (a problem refuses the start) and every
/// five minutes (a problem is logged and counted for the alarm). For each producer here with a
/// <c>Broker</c> consumer, a topic must be configured, and that topic must have a raw-delivery
/// subscription to the consumer's service queue whose filter lets those event types through. The
/// producer checks its own topic because its role is the only one that may read it (s4-l10): the
/// monolith's role names no Payments topic, and Payments' names no topic of the monolith's modules.
/// For each module here that gets a producer's events through the broker, this host must read that
/// module's service queue.
/// </summary>
public sealed partial class SubscriptionCheck(
    IAmazonSimpleNotificationService sns,
    BrokerOptions options,
    BrokerPlan plan,
    EventRoutes routes,
    IEnumerable<PublishedEvent> published,
    ILogger<SubscriptionCheck> logger) : BackgroundService
{
    public const string MeterName = "CrowdFunding.Messaging";

    private static readonly Meter Meter = new(MeterName);

    private int _problems;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var problems = await Problems(cancellationToken);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The broker is not ready for this program's routes:\n - " + string.Join("\n - ", problems));
        }

        Meter.CreateObservableGauge("broker.subscription_problems", () => _problems, description: "Broker routes of this host without their subscription");
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.CheckEvery, stoppingToken);
                try
                {
                    var problems = await Problems(stoppingToken);
                    _problems = problems.Count;
                    foreach (var problem in problems)
                    {
                        LogProblem(logger, problem);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogCheckFailed(logger, e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>What is missing for this host's <c>Broker</c> routes; empty when nothing is.</summary>
    public async Task<IReadOnlyList<string>> Problems(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var sample = new OutboxRecord("Producer", Guid.NewGuid(), "EventType", "{}", "00-00000000000000000000000000000001-0000000000000001-01");
        try
        {
            SnsEventPublisher.AttributesOf(sample);
        }
        catch (InvalidOperationException e)
        {
            problems.Add(e.Message);
        }

        foreach (var producer in published.Select(p => p.Module).Distinct())
        {
            var brokered = routes.Subscriptions.Events.Where(e => e.Value.Producer == producer)
                .Any(e => e.Value.Consumers.Any(c => routes.RouteOf(c, producer) == EventRoute.Broker));
            if (brokered && !options.Topics.ContainsKey(producer))
            {
                problems.Add($"{producer} publishes to the broker but has no topic (Events:Broker:Topics:{producer}).");
            }
        }

        var routed = routes.Subscriptions.Events
            .SelectMany(e => e.Value.Consumers.Select(consumer => (Type: e.Key, e.Value.Producer, Consumer: consumer)))
            .Where(e => routes.RouteOf(e.Consumer, e.Producer) == EventRoute.Broker)
            .ToList();

        // What this host's modules take from the broker arrives in their service's queue, which it must read.
        // A host that reads a queue none of whose modules it runs would hide their messages from the hosts that do
        // (lesson s5-l03: the monolith that set Notifications' mode to Remote must stop reading its queue).
        foreach (var service in options.Read)
        {
            var key = plan.Services.Keys.FirstOrDefault(k => string.Equals(k, service, StringComparison.OrdinalIgnoreCase));
            var served = key is null ? [] : plan.Services[key].Concat(options.AlsoServe.GetValueOrDefault(key) ?? []).ToList();
            if (!served.Any(routes.RunsHere))
            {
                problems.Add($"This host reads the {service} queue but runs none of the modules it serves ({string.Join(", ", served)}) (Events:Broker:Read).");
            }
        }

        foreach (var inbound in routed.Where(e => routes.RunsHere(e.Consumer)).GroupBy(e => (e.Producer, Service: plan.ServiceOf(e.Consumer))))
        {
            var (producer, service) = inbound.Key;
            if (!options.Read.Contains(service, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"{string.Join(", ", inbound.Select(e => e.Consumer).Distinct())} get {producer}'s events from the {service} queue, which this host does not read (Events:Broker:Read).");
            }
        }

        // What this host's modules publish to the broker must reach each consumer's queue.
        var producersHere = published.Select(p => p.Module).ToHashSet(StringComparer.Ordinal);
        foreach (var subscription in routed.Where(e => producersHere.Contains(e.Producer)).GroupBy(e => (e.Producer, Service: plan.ServiceOf(e.Consumer))))
        {
            var (producer, service) = subscription.Key;
            var types = subscription.Select(e => e.Type).Distinct().ToList();
            if (!options.Topics.TryGetValue(producer, out var topic) || !options.Queues.TryGetValue(service, out var queue))
            {
                problems.Add($"No topic for {producer} or no queue for {service} is configured (Events:Broker:Topics, Events:Broker:Queues).");
                continue;
            }

            var passes = await Filtered(topic, queue.Arn, cancellationToken);
            if (passes is null)
            {
                problems.Add($"The {producer} topic has no raw-delivery subscription to the {service} queue.");
                continue;
            }

            var missing = types.Where(t => !passes.Contains(t)).ToList();
            if (missing.Count > 0)
            {
                problems.Add($"The {producer} topic's subscription to the {service} queue filters out {string.Join(", ", missing)}.");
            }
        }

        return problems;
    }

    /// <summary>The event types the topic's raw-delivery subscriptions to the queue let through, or null when it has none.</summary>
    private async Task<HashSet<string>?> Filtered(string topicArn, string queueArn, CancellationToken cancellationToken)
    {
        HashSet<string>? passes = null;
        string? next = null;
        do
        {
            var page = await sns.ListSubscriptionsByTopicAsync(topicArn, next, cancellationToken);
            foreach (var subscription in (page.Subscriptions ?? []).Where(s => s.Endpoint == queueArn && s.Protocol == "sqs"))
            {
                var attributes = (await sns.GetSubscriptionAttributesAsync(subscription.SubscriptionArn, cancellationToken)).Attributes ?? [];
                if (attributes.GetValueOrDefault("RawMessageDelivery") != "true"
                    || attributes.GetValueOrDefault("FilterPolicyScope", "MessageAttributes") != "MessageAttributes")
                {
                    continue;
                }

                passes ??= [];
                passes.UnionWith(EventTypesOf(attributes.GetValueOrDefault("FilterPolicy")));
            }

            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        return passes;
    }

    /// <summary>The exact <c>event_type</c> values a filter policy names; the course's policies name nothing else.</summary>
    private static IEnumerable<string> EventTypesOf(string? filterPolicy)
    {
        if (string.IsNullOrEmpty(filterPolicy))
        {
            return [];
        }

        using var policy = JsonDocument.Parse(filterPolicy);
        return policy.RootElement.TryGetProperty(MessageAttributes.EventType, out var values) && values.ValueKind == JsonValueKind.Array
            ? [.. values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Broker route problem: {Problem}")]
    private static partial void LogProblem(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the broker's subscriptions; the next check tries again")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
