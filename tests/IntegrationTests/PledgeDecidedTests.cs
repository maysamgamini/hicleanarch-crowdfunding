using System.Net.Http.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Notifications;

using Npgsql;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>Each backer hears the decision by their own pledge's PledgeDecided, once, whichever the outcome.</summary>
public sealed class PledgeDecidedTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Told(WebApplicationFactory<Program> host, Guid backer, string subject) =>
        host.Services.GetRequiredService<InMemoryEmailSender>().Sent.Count(m => m.RecipientId == backer && m.Subject == subject);

    private static async Task Close(WebApplicationFactory<Program> host, Guid campaignId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaignId), Ct);
    }

    /// <summary>A published campaign with a pledge from each of <paramref name="backers"/>, past its deadline.</summary>
    private async Task<(Scenario Given, Scenario.Published Campaign, List<Guid> Backers)> Pledged(WebApplicationFactory<Program> host, long goalMinor, int backers)
    {
        var given = new Scenario(database, host);
        var campaign = await given.Publish(goalMinor);
        var ids = Enumerable.Range(0, backers).Select(_ => Guid.NewGuid()).ToList();
        foreach (var backer in ids)
        {
            (await given.Backer(backer).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        }

        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        return (given, campaign, ids);
    }

    [Theory]
    [InlineData(1_000, "The campaign is funded")]
    [InlineData(100_000, "The campaign was not funded")]
    public async Task Every_backer_is_told_the_outcome_once_by_their_own_pledge(long goalMinor, string subject)
    {
        await using var host = database.StartHost();
        var (given, campaign, backers) = await Pledged(host, goalMinor, 3);

        await Close(host, campaign.CampaignId);

        Assert.Equal(3L, await given.Scalar<long>(
            "SELECT count(DISTINCT payload::jsonb ->> 'pledgeId') FROM pledges.outbox WHERE type = 'PledgeDecided' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        foreach (var backer in backers)
        {
            await Scenario.Eventually(() => Task.FromResult(Told(host, backer, subject) == 1));
        }
    }

    [Fact]
    public async Task A_pledge_decision_delivered_again_sends_no_second_email()
    {
        await using var host = database.StartHost();
        var (given, campaign, backers) = await Pledged(host, 100_000, 1);
        await Close(host, campaign.CampaignId);
        await Scenario.Eventually(() => Task.FromResult(Told(host, backers[0], "The campaign was not funded") == 1));

        // The same outbox row handed to the handlers again, as a sweep or a broker's redelivery would.
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new NpgsqlCommand(
            "SELECT id, payload::text FROM pledges.outbox WHERE type = 'PledgeDecided' AND payload::jsonb ->> 'campaignId' = @id::text", owner);
        read.Parameters.AddWithValue("id", campaign.CampaignId.ToString());
        OutboxRecord record;
        await using (var row = await read.ExecuteReaderAsync(Ct))
        {
            Assert.True(await row.ReadAsync(Ct));
            record = new OutboxRecord("Pledges", row.GetGuid(0), "PledgeDecided", row.GetString(1));
        }

        await host.Services.GetRequiredService<OutboxDispatcher>().Deliver(record, Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // a second email, if one were queued, would be sent by now

        Assert.Equal(1, Told(host, backers[0], "The campaign was not funded"));
    }

    [Fact]
    public async Task An_unsuccessful_campaigns_telling_resumes_from_its_cursor_and_tells_each_pledge_once()
    {
        // Two pledges a batch, one batch a pass: each pass stops where a pass that died would.
        await using var host = database.StartHost().WithWebHostBuilder(web => web
            .UseSetting("Pledges:MarkBatchSize", "2")
            .UseSetting("Pledges:MarkBatchesPerPass", "1"));
        var (given, campaign, _) = await Pledged(host, 100_000, 5);
        async Task<long> Told() => await given.Scalar<long>(
            "SELECT count(*) FROM pledges.outbox WHERE type = 'PledgeDecided' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId);

        await Close(host, campaign.CampaignId);
        Assert.Equal(2L, await Told());
        Assert.False(await given.Scalar<bool>("SELECT told_finished_at IS NOT NULL FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));

        await Close(host, campaign.CampaignId);
        await Close(host, campaign.CampaignId);
        await Close(host, campaign.CampaignId);

        Assert.Equal(5L, await Told());
        Assert.Equal(5L, await given.Scalar<long>(
            "SELECT count(DISTINCT payload::jsonb ->> 'pledgeId') FROM pledges.outbox WHERE type = 'PledgeDecided' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        Assert.True(await given.Scalar<bool>("SELECT told_finished_at IS NOT NULL FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }
}
