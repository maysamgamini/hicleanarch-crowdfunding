using System.Collections.Concurrent;
using System.Diagnostics;

using BuildingBlocks.Infrastructure.Events;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>What an operator sees: traces that cross modules, and one number per Round 1 failure mode.</summary>
public sealed class OperationsTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task An_events_delivery_continues_the_trace_of_the_request_that_wrote_it()
    {
        var delivered = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is OutboxDispatcher.ActivitySourceName or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => delivered.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);
        var given = new Scenario(database, _host);

        var campaign = await given.Publish();

        var traceParent = await given.Scalar<string>(
            "SELECT trace_parent FROM campaigns.outbox WHERE type = 'CampaignPublished' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId);
        Assert.False(string.IsNullOrEmpty(traceParent));
        var requestTrace = ActivityContext.Parse(traceParent, null).TraceId;
        await Scenario.Eventually(() => Task.FromResult(delivered.Any(a => a.OperationName == "deliver CampaignPublished" && a.TraceId == requestTrace)));
        Assert.Contains(delivered, a => a.OperationName.StartsWith("handle Pledges.", StringComparison.Ordinal) && a.TraceId == requestTrace);
    }

    [Fact]
    public async Task A_campaign_left_past_its_deadline_shows_on_the_gauge()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '10 minutes' WHERE campaign_id = @id", campaign.CampaignId);

        // A new host measures as it starts.
        using var restarted = database.StartHost();
        _ = restarted.Server;

        await Scenario.Eventually(() => Task.FromResult(Pledges.Infrastructure.PledgesMonitor.LivePastDeadline.Value >= 1));
    }

    [Fact]
    public async Task A_display_total_that_keeps_differing_from_its_ledger_is_counted()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(); // no pledges: its ledger sums to 0

        // A store whose total for this campaign is wrong, whatever the aggregator writes. The check
        // is run directly: its gauge is shared by every test host in the process.
        using var host = database.StartHost(services => services.AddSingleton<ITotalsStore>(new WrongFor(campaign.CampaignId)));
        await using var scope = host.Services.CreateAsyncScope();

        var different = await Pledges.Infrastructure.TotalsCheck.FindDifferences(scope.ServiceProvider, TestContext.Current.CancellationToken);

        Assert.Equal([campaign.CampaignId], different);
    }

    private sealed class WrongFor(Guid campaignId) : ITotalsStore
    {
        public Task Set(Guid campaign, DisplayTotals totals, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DisplayTotals?> Get(Guid campaign, CancellationToken cancellationToken) =>
            Task.FromResult(campaign == campaignId ? new DisplayTotals(1, 0, new Dictionary<Guid, int>(), DateTimeOffset.UtcNow) : null);
    }
}
