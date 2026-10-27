using System.Globalization;

using BuildingBlocks.Infrastructure.Events;

using Microsoft.Extensions.DependencyInjection;

namespace CrowdFunding.Messaging;

/// <summary>
/// <c>replay-events --module Pledges --from 2026-10-27T10:00Z --to 2026-10-27T11:00Z [--type PledgeChargeRequested]</c>:
/// publishes again the rows of one module's outbox already published in a time range, for a topic that
/// had no subscription at the time, or after a redrive. Consumers handle each event once (inbox), so a
/// replayed event they already have does nothing. An event type listed in <c>events/retired.json</c> is never
/// published again: it has left its contract, and no consumer may still read it.
/// </summary>
public static class EventReplay
{
    private const int Page = 1000;

    public static Task<int> Run(IServiceProvider services, IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken) =>
        Run(services, args, output, Retired(Path.Combine(AppContext.BaseDirectory, "events", "retired.json")), cancellationToken);

    /// <summary>The retired event types, as <c>Producer.Name</c>; none when the file is absent.</summary>
    public static IReadOnlySet<string> Retired(string path)
    {
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("retired").EnumerateArray().Select(t => t.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    public static async Task<int> Run(IServiceProvider services, IReadOnlyList<string> args, TextWriter output, IReadOnlySet<string> retired, CancellationToken cancellationToken)
    {
        string? Option(string name)
        {
            var at = args.ToList().IndexOf("--" + name);
            return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
        }

        var moduleName = Option("module");
        if (moduleName is null || !DateTimeOffset.TryParse(Option("from"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from)
            || !DateTimeOffset.TryParse(Option("to"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to) || to <= from)
        {
            await output.WriteLineAsync("usage: replay-events --module <Module> --from <time> --to <time> [--type <EventType>]");
            return 2;
        }

        var type = Option("type");
        if (type is not null && retired.Contains($"{moduleName}.{type}"))
        {
            await output.WriteLineAsync($"{moduleName}.{type} is retired (events/retired.json): it is never published again.");
            return 2;
        }

        var module = services.GetServices<IModuleEvents>().SingleOrDefault(m => m.Module == moduleName);
        if (module is null)
        {
            await output.WriteLineAsync($"This program has no module {moduleName}.");
            return 2;
        }

        var publisher = services.GetRequiredService<IEventPublisher>();
        int sent = 0, refused = 0, skipped = 0;
        OutboxRecord? after = null;
        IReadOnlyList<OutboxRecord> page;
        do
        {
            page = await module.Published(type, from, to, after, Page, cancellationToken);
            var live = page.Where(r => !retired.Contains($"{moduleName}.{r.Type}")).ToList();
            skipped += page.Count - live.Count;
            foreach (var chunk in live.Chunk(100))
            {
                var accepted = await publisher.Publish(chunk, cancellationToken);
                sent += accepted.Count;
                refused += chunk.Length - accepted.Count;
            }

            after = page.Count > 0 ? page[^1] : after;
        }
        while (page.Count == Page);

        await output.WriteLineAsync($"Replayed {sent} {type ?? "events"} of {moduleName} from {from:O} to {to:O}; {refused} refused; {skipped} retired, not sent.");
        return refused == 0 ? 0 : 1;
    }
}
