using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l03: Notifications runs in its own host beside the monolith, both reading its queue, until
/// the monolith sets Modules:Notifications:Mode = Remote. A second host that runs the module stands in
/// for Notifications.Host here: the same module, the same queue and inbox (its own tests start the real
/// host, tests/Notifications.Host.Tests).
/// </summary>
public sealed class NotificationsModeTests(Database database) : IDisposable
{
    private const string Backed = "You're a backer";

    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private static int SentTo(Guid user, string subject) => Database.Mail.Sent.Count(m => m.RecipientId == user && m.Subject == subject);

    private WebApplicationFactory<Program> Host(IReadOnlyDictionary<string, string?> broker, bool remote = false)
    {
        var settings = broker;
        if (remote)
        {
            // Remote: the monolith runs no Notifications, so it reads only its own queue.
            var own = broker.Where(kv => !kv.Key.StartsWith("Events:Broker:Read:", StringComparison.OrdinalIgnoreCase)).ToDictionary();
            own["Events:Broker:Read:0"] = "monolith";
            settings = own;
        }

        var host = database.StartHost(settings).WithWebHostBuilder(web => web.UseSetting("Modules:Notifications:Mode", remote ? "Remote" : "InProcess"));
        _hosts.Add(host);
        _ = host.Services; // started
        return host;
    }

    [Fact]
    public async Task Two_hosts_reading_the_notifications_queue_send_one_email_per_event()
    {
        var broker = database.Provision().Settings;
        var monolith = Host(broker);
        Host(broker); // the second host that runs Notifications, as Notifications.Host does beside the monolith
        var given = new Scenario(database, monolith);
        var campaign = await given.Publish();
        var backers = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        foreach (var backer in backers)
        {
            (await given.Backer(backer).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();
        }

        await Scenario.Eventually(() => Task.FromResult(backers.All(b => SentTo(b, Backed) >= 1)));
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // a second copy would have been sent by now
        Assert.All(backers, b => Assert.Equal(1, SentTo(b, Backed)));
    }

    [Fact]
    public async Task Remote_mode_starts_only_once_no_outbox_row_owes_Notifications_an_in_process_delivery()
    {
        // A row only Notifications could take in process: no host has a handler for its type, so none delivers it.
        var id = Guid.CreateVersion7();
        await database.Execute($"""
            INSERT INTO campaigns.outbox (id, type, version, occurred_at, payload, attempts, sinks)
            VALUES ('{id}', 'NotificationsModeTestEvent', 1, now(), jsonb_build_object(), 0, ARRAY['Notifications'])
            """);
        var broker = database.Provision().Settings;

        var refused = Assert.ThrowsAny<Exception>(() => Host(broker, remote: true));
        await database.Execute($"UPDATE campaigns.outbox SET delivered_at = now() WHERE id = '{id}'");
        var remote = Host(broker, remote: true);

        Assert.Contains("This host no longer runs Notifications, but outbox rows still owe it an in-process delivery", refused.ToString(), StringComparison.Ordinal);
        Assert.NotNull(remote.Services);
    }

    [Fact]
    public async Task Back_to_InProcess_the_queued_events_are_each_emailed_once()
    {
        var broker = database.Provision().Settings;
        var remote = Host(broker, remote: true);
        var given = new Scenario(database, remote);
        var campaign = await given.Publish();
        var backer = Guid.NewGuid();
        (await given.Backer(backer).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();

        // Nothing runs Notifications: its events wait in its queue.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(0, SentTo(backer, Backed));

        // The rollback: a host that runs Notifications again reads the queue.
        Host(broker);
        await Scenario.Eventually(() => Task.FromResult(SentTo(backer, Backed) == 1));
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(1, SentTo(backer, Backed));
        Assert.Equal(1L, await Handled(campaign.CampaignId));
    }

    private async Task<long> Handled(Guid campaignId)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM notifications.campaign_creators WHERE campaign_id = @id", connection);
        command.Parameters.AddWithValue("id", campaignId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
