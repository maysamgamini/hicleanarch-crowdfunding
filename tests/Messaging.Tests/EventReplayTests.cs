using CrowdFunding.Messaging;

using Microsoft.Extensions.DependencyInjection;

namespace Messaging.Tests;

/// <summary>replay-events never publishes an event type that has left its contract (events/retired.json).</summary>
public class EventReplayTests
{
    [Fact]
    public async Task A_retired_event_type_is_refused_before_anything_is_read()
    {
        var output = new StringWriter();
        var retired = new HashSet<string>(StringComparer.Ordinal) { "Campaigns.CampaignCreatorRecorded" };

        var exit = await EventReplay.Run(new ServiceCollection().BuildServiceProvider(),
            ["--module", "Campaigns", "--type", "CampaignCreatorRecorded", "--from", "2026-10-01T00:00Z", "--to", "2026-10-02T00:00Z"],
            output, retired, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.Contains("Campaigns.CampaignCreatorRecorded is retired", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_retired_list_ships_with_the_host_and_names_producer_and_type()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "events", "retired.json");
        Assert.True(File.Exists(path), path);

        var retired = EventReplay.Retired(path);

        Assert.All(retired, t => Assert.Matches(@"^[A-Z][A-Za-z]+\.[A-Z][A-Za-z]+$", t));
    }
}
