using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Campaigns.Application;
using Campaigns.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;

using Pledges.Contracts;

namespace IntegrationTests;

/// <summary>The status people see follows Pledges' and Payments' events, in whatever order they arrive.</summary>
public sealed class CampaignStatusTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    /// <summary>One Campaigns handler, in a transaction of its own, as the inbox runs it.</summary>
    private async Task Handle<TEvent, THandler>(TEvent integrationEvent)
        where TEvent : IntegrationEvent
        where THandler : IIntegrationEventHandler<TEvent>
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<THandler>().Handle(integrationEvent, Ct);
        await transaction.CommitAsync(Ct);
    }

    private Task Decided(Guid campaignId, string outcome) =>
        Handle<CampaignFundingDecided, FollowFundingDecision>(
            new CampaignFundingDecided(Guid.CreateVersion7(), DateTimeOffset.UtcNow, campaignId, outcome, 6_000, 1, 5_000));

    private Task Settled(Guid campaignId) =>
        Handle<CampaignSettled, FollowSettlement>(new CampaignSettled(Guid.CreateVersion7(), DateTimeOffset.UtcNow, campaignId, 1, 0));

    private static Task<string> Stored(Scenario given, Scenario.Published campaign) =>
        given.Scalar<string>("SELECT status FROM campaigns.campaigns WHERE id = @id", campaign.CampaignId);

    [Fact]
    public async Task An_unsuccessful_decision_ends_released_and_the_page_says_so()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(50_000);

        await Decided(campaign.CampaignId, CampaignFundingDecided.Unsuccessful);

        Assert.Equal("RELEASED", await Stored(given, campaign));
        var read = await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct);
        Assert.Equal("RELEASED", read.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_repeated_or_contrary_decision_changes_a_released_campaign_nothing()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(50_000);
        await Decided(campaign.CampaignId, CampaignFundingDecided.Unsuccessful);

        await Decided(campaign.CampaignId, CampaignFundingDecided.Unsuccessful);
        await Decided(campaign.CampaignId, CampaignFundingDecided.Funded);
        await Settled(campaign.CampaignId);

        Assert.Equal("RELEASED", await Stored(given, campaign));
    }

    [Fact]
    public async Task A_settlement_that_arrives_before_its_decision_still_ends_settled()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000);

        await Settled(campaign.CampaignId);
        Assert.Equal("SETTLED", await Stored(given, campaign));

        await Decided(campaign.CampaignId, CampaignFundingDecided.Funded);
        Assert.Equal("SETTLED", await Stored(given, campaign));
    }

    [Fact]
    public async Task In_the_usual_order_a_funded_campaign_ends_settled()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000);

        await Decided(campaign.CampaignId, CampaignFundingDecided.Funded);
        Assert.Equal("FUNDED", await Stored(given, campaign));
        await Settled(campaign.CampaignId);

        Assert.Equal("SETTLED", await Stored(given, campaign));
    }

    [Fact]
    public async Task A_settlement_never_moves_a_cancelled_campaign()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000);
        (await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();

        await Settled(campaign.CampaignId);

        Assert.Equal("CANCELLED", await Stored(given, campaign));
    }
}
