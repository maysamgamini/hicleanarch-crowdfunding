using System.Net;
using System.Net.Http.Json;

using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Notifications;

using Pledges.Application;
using Pledges.Contracts;

namespace IntegrationTests;

/// <summary>People are told what happened, once per event and person, as they asked.</summary>
public sealed class NotificationTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private InMemoryEmailSender Mail => _host.Services.GetRequiredService<InMemoryEmailSender>();

    private int SentTo(Guid user, string? subject = null) => Mail.Sent.Count(m => m.RecipientId == user && (subject is null || m.Subject == subject));

    [Fact]
    public async Task A_backer_is_told_their_pledge_counts()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = Guid.NewGuid();

        (await given.Backer(backer).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();

        await Scenario.Eventually(() => Task.FromResult(SentTo(backer, "You're a backer") == 1));
    }

    [Fact]
    public async Task Every_backer_hears_the_outcome_and_a_cancelled_backer_does_not()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(100_000);
        var (stays, leaves) = (Guid.NewGuid(), Guid.NewGuid());
        (await given.Backer(stays).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        var placed = await given.Backer(leaves).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);
        (await given.Backer(leaves).DeleteAsync(placed.Headers.Location, Ct)).EnsureSuccessStatusCode();
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM notifications.campaign_backers WHERE campaign_id = @id", campaign.CampaignId) == 1);

        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(), Ct);
        }

        await Scenario.Eventually(() => Task.FromResult(SentTo(stays, "The campaign was not funded") == 1));
        Assert.Equal(0, SentTo(leaves, "The campaign was not funded"));
    }

    [Fact]
    public async Task A_redelivered_event_sends_nothing_twice()
    {
        var backer = Guid.NewGuid();
        var activated = new PledgeActivated(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), backer, null, 6_000, "USD");

        // The handler itself, twice, past the inbox: the sent_notifications row is what stops the second send.
        for (var i = 0; i < 2; i++)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<Handlers>().Handle(activated, Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(1, SentTo(backer));
    }

    [Fact]
    public async Task A_kind_turned_off_is_not_sent()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = Guid.NewGuid();
        var client = given.Backer(backer);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/v1/me/notifications/pledge_confirmed", new { enabled = false }, Ct)).StatusCode);

        (await client.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM notifications.campaign_backers WHERE campaign_id = @id", campaign.CampaignId) == 1);

        Assert.Equal(0, SentTo(backer));
    }
}
