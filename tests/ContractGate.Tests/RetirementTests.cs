using System.Text.Json.Nodes;

namespace ContractGate.Tests;

/// <summary>When an old command or event may leave its contract: never while anything can still send or hold it.</summary>
public class RetirementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_command_cannot_be_removed_while_a_running_or_previous_version_still_sends_it()
    {
        // Campaigns v1 sends CancelFunding in process; from v2 it no longer does.
        var repository = new Versions()
            .Add("v1", f => f.Expectations("Campaigns", e => e["inProcess"] = new JsonArray("Pledges.CancelFunding")))
            .Add("v2")
            .Add("v3");

        var running = Retirement.Check(repository, Deployed.Running(("campaigns", "v1", null)), "Pledges.CancelFunding", ContractKind.CommandOrQuery, "v3", null, Now);
        var rollbackTarget = Retirement.Check(repository, Deployed.Running(("campaigns", "v2", "v1")), "Pledges.CancelFunding", ContractKind.CommandOrQuery, "v3", null, Now);
        var gone = Retirement.Check(repository, Deployed.Running(("campaigns", "v3", "v2")), "Pledges.CancelFunding", ContractKind.CommandOrQuery, "v3", null, Now);

        Assert.Contains("Campaigns in campaigns (running, v1) still uses Pledges.CancelFunding", running.Problems);
        Assert.Equal(["Campaigns in campaigns (previous, v1) still uses Pledges.CancelFunding"], rollbackTarget.Problems);
        Assert.True(gone.Passed, string.Join("\n", gone.Problems));
    }

    [Fact]
    public void An_event_still_read_or_published_by_a_recorded_version_cannot_be_removed()
    {
        // CampaignCreatorRecorded, the one-off event of s5-l05.
        var repository = WithCreatorEvent();

        var result = Retirement.Check(repository, Deployed.Running(("campaigns", "writes", null), ("pledges", "reads", null)), Creator, ContractKind.Event, "gone", Quiet, Now);

        Assert.Contains("Campaigns in campaigns (running, writes) still publishes Campaigns.CampaignCreatorRecorded", result.Problems);
        Assert.Contains("Pledges in pledges (running, reads) still uses Campaigns.CampaignCreatorRecorded", result.Problems);
    }

    [Fact]
    public void An_event_written_in_the_last_14_days_may_still_wait_in_a_dead_letter_queue()
    {
        var result = Retirement.Check(WithCreatorEvent(), NobodyUsesIt, Creator, ContractKind.Event, "gone", new OutboxFacts(Now.AddDays(-13), 0), Now);

        Assert.Contains(result.Problems, p => p.StartsWith("Campaigns.CampaignCreatorRecorded was last written", StringComparison.Ordinal));
    }

    [Fact]
    public void An_event_with_outbox_rows_still_owed_cannot_be_removed()
    {
        var result = Retirement.Check(WithCreatorEvent(), NobodyUsesIt, Creator, ContractKind.Event, "gone", new OutboxFacts(Now.AddDays(-20), 3), Now);

        Assert.Equal(["3 outbox rows of Campaigns.CampaignCreatorRecorded still owe a delivery"], result.Problems);
    }

    [Fact]
    public void An_event_replay_could_publish_again_cannot_be_removed()
    {
        var result = Retirement.Check(WithCreatorEvent(retired: false), NobodyUsesIt, Creator, ContractKind.Event, "gone", Quiet, Now);

        Assert.Equal(["replay-events could publish Campaigns.CampaignCreatorRecorded again: list it in events/retired.json"], result.Problems);
    }

    [Fact]
    public void An_event_needs_its_producers_outbox_to_be_read()
    {
        var result = Retirement.Check(WithCreatorEvent(), NobodyUsesIt, Creator, ContractKind.Event, "gone", outbox: null, Now);

        Assert.Contains("Campaigns.CampaignCreatorRecorded is an event: the producer's outbox must be read (pass --database)", result.Problems);
    }

    [Fact]
    public void An_event_that_nothing_uses_holds_or_can_replay_may_be_removed()
    {
        var result = Retirement.Check(WithCreatorEvent(), NobodyUsesIt, Creator, ContractKind.Event, "gone", Quiet, Now);

        Assert.True(result.Passed, string.Join("\n", result.Problems));
    }

    private const string Creator = "Campaigns.CampaignCreatorRecorded";

    private static readonly OutboxFacts Quiet = new(Now.AddDays(-15), 0);

    private static readonly IReadOnlyDictionary<string, DeployedService> NobodyUsesIt =
        Deployed.Running(("campaigns", "gone", "stopped"), ("pledges", "gone", "stopped"));

    /// <summary>
    /// CampaignCreatorRecorded, the one-off event of s5-l05, as checked in: Campaigns publishes it and Pledges
    /// reads it. "writes": only Campaigns' side; "reads": only Pledges' side; "stopped": neither; "gone": the event
    /// is out of the contract, listed in events/retired.json.
    /// </summary>
    private static Versions WithCreatorEvent(bool retired = true) => new Versions()
        .Add("writes", NotRead)
        .Add("reads", NotPublished)
        .Add("stopped", f =>
        {
            NotRead(f);
            NotPublished(f);
        })
        .Add("gone", f =>
        {
            NotRead(f);
            NotPublished(f);
            if (retired)
            {
                f.Retired(Creator);
            }
        });

    private static void NotRead(Files f) => f.Expectations("Pledges", e => e["events"]!.AsObject().Remove(Creator));

    private static void NotPublished(Files f) => f.Contract("Campaigns", c => c["events"]!.AsObject().Remove("CampaignCreatorRecorded"));
}
