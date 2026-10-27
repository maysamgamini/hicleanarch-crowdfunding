using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Campaigns.Contracts;

using Microsoft.AspNetCore.Hosting;
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

    private static InMemoryEmailSender Mail => Database.Mail;

    /// <summary>The Notifications service beside the monolith (s5-l04): its handlers, storage and digests.</summary>
    private IServiceProvider Notifications => _host.Notifications().Services;

    private static int SentTo(Guid user, string? subject = null) => Mail.Sent.Count(m => m.RecipientId == user && (subject is null || m.Subject == subject));

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
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM notifications.campaign_pledges WHERE campaign_id = @id AND activated_at IS NOT NULL", campaign.CampaignId) == 2);
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM notifications.campaign_pledges WHERE campaign_id = @id AND cancelled_at IS NOT NULL", campaign.CampaignId) == 1);

        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaign.CampaignId), Ct);
        }

        await Scenario.Eventually(() => Task.FromResult(SentTo(stays, "The campaign was not funded") == 1));
        Assert.Equal(0, SentTo(leaves, "The campaign was not funded"));
    }

    [Fact]
    public async Task A_redelivered_event_sends_nothing_twice()
    {
        var backer = Guid.NewGuid();
        var activated = new PledgeActivated(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), backer, null, 6_000, "USD");

        // The handler itself, twice, past the inbox: the outgoing_emails row is what stops the second send.
        for (var i = 0; i < 2; i++)
        {
            await using var scope = Notifications.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<Handlers>().Handle(activated, Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(1L, await database.Count("notifications.outgoing_emails", "recipient_id::text", backer.ToString()));
        await Scenario.Eventually(() => Task.FromResult(SentTo(backer) == 1));
    }

    /// <summary>One Notifications handler call, in a transaction of its own, as the inbox runs it.</summary>
    private async Task Handle<TEvent>(TEvent integrationEvent)
        where TEvent : IntegrationEvent
    {
        await using var scope = Notifications.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await ((IIntegrationEventHandler<TEvent>)(object)scope.ServiceProvider.GetRequiredService<Handlers>()).Handle(integrationEvent, Ct);
        await transaction.CommitAsync(Ct);
    }

    private static PledgeActivated Activated(Guid pledgeId, Guid campaignId, Guid backerId) =>
        new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, pledgeId, campaignId, backerId, null, 6_000, "USD");

    private static PledgeCancelled Cancelled(Guid pledgeId, Guid campaignId, Guid backerId) =>
        new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, pledgeId, campaignId, backerId, 6_000);

    /// <summary>Whether the backer is on the campaign's list: a live pledge of theirs, activated and neither cancelled nor released.</summary>
    private async Task<bool> OnTheList(Guid campaignId, Guid backerId)
    {
        await using var owner = new Npgsql.NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new Npgsql.NpgsqlCommand("""
            SELECT count(*) FROM notifications.campaign_pledges
            WHERE campaign_id = @campaign AND backer_id = @backer
              AND activated_at IS NOT NULL AND cancelled_at IS NULL AND released_at IS NULL
            """, owner);
        read.Parameters.AddWithValue("campaign", campaignId);
        read.Parameters.AddWithValue("backer", backerId);
        return (long)(await read.ExecuteScalarAsync(Ct))! > 0;
    }

    /// <summary>A backer cancels P1 and pledges again with P2: the cancel and the new activation arrive either way round.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cancel_of_one_pledge_and_the_activation_of_the_next_keep_the_backer_whichever_comes_first(bool cancelFirst)
    {
        var (campaign, backer, first, second) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Handle(Activated(first, campaign, backer));

        if (cancelFirst)
        {
            await Handle(Cancelled(first, campaign, backer));
            await Handle(Activated(second, campaign, backer));
        }
        else
        {
            await Handle(Activated(second, campaign, backer));
            await Handle(Cancelled(first, campaign, backer));
        }

        Assert.True(await OnTheList(campaign, backer));
    }

    /// <summary>One pledge's activation and its cancel arrive either way round: the backer is off the list.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_pledge_activated_and_cancelled_leaves_the_list_whichever_arrives_first(bool cancelFirst)
    {
        var (campaign, backer, pledge) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        if (cancelFirst)
        {
            await Handle(Cancelled(pledge, campaign, backer));
            await Handle(Activated(pledge, campaign, backer));
        }
        else
        {
            await Handle(Activated(pledge, campaign, backer));
            await Handle(Cancelled(pledge, campaign, backer));
        }

        Assert.False(await OnTheList(campaign, backer));
    }

    [Theory]
    [InlineData(CampaignFundingDecided.Funded, "Your campaign is funded", "It raised 120% of its goal from 2 backers.")]
    [InlineData(CampaignFundingDecided.Unsuccessful, "Your campaign was not funded", "It raised 12% of its goal from 2 backers.")]
    public async Task The_creator_hears_the_decision_whichever_it_is(string outcome, string subject, string raised)
    {
        var (campaign, creator) = (Guid.NewGuid(), Guid.NewGuid());
        await Handle(new CampaignPublished(Guid.CreateVersion7(), DateTimeOffset.UtcNow, campaign, creator, 50_000, "USD", DateTimeOffset.UtcNow.AddDays(30), []));

        await Handle(new CampaignFundingDecided(Guid.CreateVersion7(), DateTimeOffset.UtcNow, campaign, outcome,
            outcome == CampaignFundingDecided.Funded ? 60_000 : 6_000, 2, 50_000));

        await Scenario.Eventually(() => Task.FromResult(SentTo(creator, subject) == 1));
        Assert.StartsWith(raised, Mail.Sent.Single(m => m.RecipientId == creator).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_creator_gets_one_digest_a_day_instead_of_an_email_per_pledge()
    {
        var (campaign, creator) = (Guid.NewGuid(), Guid.NewGuid());
        var day = new DateOnly(2026, 1, 15);
        var noon = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await Handle(new CampaignPublished(Guid.CreateVersion7(), noon, campaign, creator, 50_000, "USD", noon.AddDays(30), []));
        var pledges = Enumerable.Range(0, 3).Select(_ => (Pledge: Guid.NewGuid(), Backer: Guid.NewGuid())).ToList();
        foreach (var (pledge, backer) in pledges)
        {
            await Handle(new PledgeActivated(Guid.CreateVersion7(), noon, pledge, campaign, backer, null, 6_000, "USD"));
        }

        await Handle(new PledgeCancelled(Guid.CreateVersion7(), noon.AddHours(1), pledges[0].Pledge, campaign, pledges[0].Backer, 6_000));
        await Handle(new PledgeCancelled(Guid.CreateVersion7(), noon.AddDays(1), pledges[1].Pledge, campaign, pledges[1].Backer, 6_000)); // the next day's
        var (earlier, earlierBacker) = (Guid.NewGuid(), Guid.NewGuid()); // made the day before, cancelled on the day
        await Handle(new PledgeActivated(Guid.CreateVersion7(), noon.AddDays(-1), earlier, campaign, earlierBacker, null, 6_000, "USD"));
        await Handle(new PledgeCancelled(Guid.CreateVersion7(), noon.AddHours(2), earlier, campaign, earlierBacker, 6_000));
        await Handle(new PledgeActivated(Guid.CreateVersion7(), noon.AddDays(1), Guid.NewGuid(), campaign, Guid.NewGuid(), null, 6_000, "USD"));

        for (var pass = 0; pass < 2; pass++)
        {
            await using var scope = Notifications.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PledgeDigests>().Record(day, Ct);
        }

        await Scenario.Eventually(() => Task.FromResult(SentTo(creator, "Your campaign's pledges on 2026-01-15") == 1));
        Assert.Equal("On 2026-01-15 (UTC) your campaign got 3 new pledges, and 2 pledges were cancelled.", Mail.Sent.Single(m => m.RecipientId == creator).Body);
        await Task.Delay(500, Ct);
        Assert.Equal(1, SentTo(creator));
    }

    [Theory]
    [InlineData("2026-10-27T00:30:00Z", "2026-10-25")]
    [InlineData("2026-10-27T01:30:00Z", "2026-10-26")]
    public void A_days_digest_is_due_once_its_events_have_had_time_to_arrive(string now, string due) =>
        Assert.Equal(DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture),
            PledgeDigests.DueDay(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(1)));

    [Fact]
    public async Task On_a_laptop_the_kept_emails_are_listed_at_dev_emails()
    {
        await using var laptop = database.StartNotifications(null).WithWebHostBuilder(web => web.UseEnvironment("Development"));
        var user = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();
        await Database.Mail.Send(new Email(someoneElse, $"{someoneElse:N}@example.invalid", "The campaign is funded", "It reached its goal."), Ct);
        await Database.Mail.Send(new Email(user, $"{user:N}@example.invalid", "You're a backer", "Your pledge counts."), Ct);

        var listed = await laptop.CreateClient().GetFromJsonAsync<JsonElement>($"/dev/emails?to={user}", Ct);

        var email = Assert.Single(listed.EnumerateArray());
        Assert.Equal("You're a backer", email.GetProperty("subject").GetString());
        Assert.Equal(user, email.GetProperty("recipient_id").GetGuid());
    }

    [Fact]
    public async Task Outside_Development_there_is_no_dev_emails()
    {
        // Signed in, so the answer is about the route, not the deny-by-default policy.
        var token = Database.Token(BuildingBlocks.Infrastructure.Identity.Roles.Backer);

        Assert.Equal(HttpStatusCode.NotFound, (await Signed(_host.Notifications().Host.CreateClient(), token).GetAsync("/dev/emails", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Database.Client(_host, token).GetAsync("/dev/emails", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_kind_turned_off_is_not_sent()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = Guid.NewGuid();
        var client = given.Backer(backer);
        // The preference is the Notifications service's own endpoint, which the gateway's route reaches; the
        // monolith no longer has it.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/v1/me/notifications/pledge_confirmed", new { enabled = false }, Ct)).StatusCode);
        var monolithItself = Signed(_host.CreateClient(), Database.Token(backer, BuildingBlocks.Infrastructure.Identity.Roles.Backer));
        Assert.Equal(HttpStatusCode.NotFound, (await monolithItself.PutAsJsonAsync("/v1/me/notifications/pledge_confirmed", new { enabled = false }, Ct)).StatusCode);

        (await client.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM notifications.campaign_pledges WHERE campaign_id = @id AND activated_at IS NOT NULL AND cancelled_at IS NULL", campaign.CampaignId) == 1);

        Assert.Equal(0L, await database.Count("notifications.outgoing_emails", "recipient_id::text", backer.ToString()));
        Assert.Equal(0, SentTo(backer));
    }

    private static HttpClient Signed(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
