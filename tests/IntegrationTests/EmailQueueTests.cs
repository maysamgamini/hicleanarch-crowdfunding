using System.Collections.Concurrent;

using BuildingBlocks.Application;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Notifications;

using Npgsql;

using Pledges.Contracts;

namespace IntegrationTests;

/// <summary>A database of the class's own: no other test class's email worker claims its emails.</summary>
public sealed class OwnDatabase : IAsyncLifetime
{
    public Database Database { get; } = new();

    public ValueTask InitializeAsync() => Database.InitializeAsync();

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}

/// <summary>
/// Handlers only record who is to be told; the email worker sends, paced, and retries each recipient
/// on their own, so one failing address holds back nobody else.
/// </summary>
public sealed class EmailQueueTests(OwnDatabase own) : IClassFixture<OwnDatabase>
{
    private readonly Database _database = own.Database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Outcome = "The campaign was not funded";

    /// <summary>Fails the next outcome emails to some recipients, and keeps when each email went out.</summary>
    private sealed class FlakySender(InMemoryEmailSender inner) : IEmailSender
    {
        public ConcurrentDictionary<Guid, int> FailuresLeft { get; } = new();

        public ConcurrentQueue<DateTimeOffset> SentAt { get; } = new();

        public Task Send(Email email, CancellationToken cancellationToken)
        {
            if (email.Subject == Outcome && FailuresLeft.TryGetValue(email.RecipientId, out var left) && left > 0)
            {
                FailuresLeft[email.RecipientId] = left - 1;
                throw new InvalidOperationException("Throttling: maximum sending rate exceeded.");
            }

            SentAt.Enqueue(DateTimeOffset.UtcNow);
            return inner.Send(email, cancellationToken);
        }
    }

    /// <summary>Counts the user-pool lookups per user; the users in <see cref="Gone"/> no longer exist.</summary>
    private sealed class CountingDirectory : IUserDirectory
    {
        public ConcurrentDictionary<Guid, int> Lookups { get; } = new();

        public ConcurrentDictionary<Guid, bool> Gone { get; } = new();

        public Task<string?> EmailOf(Guid userId, CancellationToken cancellationToken)
        {
            Lookups.AddOrUpdate(userId, 1, (_, n) => n + 1);
            return Task.FromResult(Gone.ContainsKey(userId) ? null : $"{userId:N}@example.invalid");
        }
    }

    private WebApplicationFactory<Program> Host(params (string Key, string Value)[] settings) =>
        _database.StartHost(services =>
        {
            services.AddSingleton(sp => new FlakySender(sp.GetRequiredService<InMemoryEmailSender>()));
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<FlakySender>());
            services.AddSingleton<CountingDirectory>();
            services.AddSingleton<IUserDirectory>(sp => sp.GetRequiredService<CountingDirectory>());
        }).WithWebHostBuilder(web =>
        {
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }
        });

    /// <summary>One Notifications handler call, in a transaction of its own, as the inbox runs it.</summary>
    private static async Task Handle<TEvent>(WebApplicationFactory<Program> host, TEvent integrationEvent)
        where TEvent : IntegrationEvent
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await ((IIntegrationEventHandler<TEvent>)(object)scope.ServiceProvider.GetRequiredService<Handlers>()).Handle(integrationEvent, Ct);
        await transaction.CommitAsync(Ct);
    }

    /// <summary>A campaign's backers, as Notifications hears them, and its unsuccessful decision: the event's id.</summary>
    /// <summary>The backers have turned pledge_confirmed off, so the outcome is the one email each gets.</summary>
    private async Task<Guid> DecideWithBackers(WebApplicationFactory<Program> host, IEnumerable<Guid> backers)
    {
        var campaign = Guid.NewGuid();
        foreach (var backer in backers)
        {
            await _database.Execute($"INSERT INTO notifications.notification_preferences (user_id, kind, enabled) VALUES ('{backer}', 'pledge_confirmed', false)");
            await Handle(host, new PledgeActivated(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.NewGuid(), campaign, backer, null, 6_000, "USD"));
        }

        var decided = new CampaignFundingDecided(Guid.CreateVersion7(), DateTimeOffset.UtcNow, campaign, CampaignFundingDecided.Unsuccessful, 6_000, 1, 50_000);
        await Handle(host, decided);
        return decided.EventId;
    }

    private static int Told(WebApplicationFactory<Program> host, Guid user) =>
        host.Services.GetRequiredService<InMemoryEmailSender>().Sent.Count(m => m.RecipientId == user && m.Subject == Outcome);

    /// <summary>The status and attempts of the email to one recipient for one event, read as the owner.</summary>
    private async Task<(string Status, int Attempts)> Row(Guid eventId, Guid recipient)
    {
        await using var owner = new NpgsqlConnection(_database.Owner);
        await owner.OpenAsync(Ct);
        await using var read = new NpgsqlCommand(
            "SELECT status, attempts FROM notifications.outgoing_emails WHERE event_id = @event AND recipient_id = @recipient AND kind = 'campaign_unsuccessful'", owner);
        read.Parameters.AddWithValue("event", eventId);
        read.Parameters.AddWithValue("recipient", recipient);
        await using var row = await read.ExecuteReaderAsync(Ct);
        Assert.True(await row.ReadAsync(Ct));
        return (row.GetString(0), row.GetInt32(1));
    }

    [Fact]
    public async Task One_failing_recipient_holds_back_nobody_and_is_retried_with_its_address_kept()
    {
        await using var host = Host(("Notifications:RetryAfter", "00:00:00.100"), ("Notifications:MaxAttempts", "3"));
        var (fine, flaky, broken) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var sender = host.Services.GetRequiredService<FlakySender>();
        sender.FailuresLeft[flaky] = 2;
        sender.FailuresLeft[broken] = int.MaxValue;

        var eventId = await DecideWithBackers(host, [fine, flaky, broken]);

        await Scenario.Eventually(() => Task.FromResult(Told(host, fine) == 1));
        await Scenario.Eventually(() => Task.FromResult(Told(host, flaky) == 1));
        await Scenario.Eventually(async () => (await Row(eventId, broken)).Status == "FAILED");
        Assert.Equal(("SENT", 1), await Row(eventId, fine));
        Assert.Equal(("SENT", 3), await Row(eventId, flaky));
        Assert.Equal(("FAILED", 3), await Row(eventId, broken));
        Assert.Equal(0, Told(host, broken));
        var directory = host.Services.GetRequiredService<CountingDirectory>();
        Assert.Equal(1, directory.Lookups[flaky]); // three attempts, one lookup: the address was kept
        Assert.Equal(1, directory.Lookups[broken]);
    }

    [Fact]
    public async Task A_recipient_who_no_longer_exists_is_not_retried()
    {
        await using var host = Host(("Notifications:RetryAfter", "00:00:00.100"));
        var (stays, gone) = (Guid.NewGuid(), Guid.NewGuid());
        host.Services.GetRequiredService<CountingDirectory>().Gone[gone] = true;

        var eventId = await DecideWithBackers(host, [stays, gone]);

        await Scenario.Eventually(() => Task.FromResult(Told(host, stays) == 1));
        await Scenario.Eventually(async () => (await Row(eventId, gone)).Status == "NO_ADDRESS");
        Assert.Equal(("NO_ADDRESS", 1), await Row(eventId, gone));
    }

    /// <summary>The rate is SES's quota, so the worker never sends faster than it is set to, whatever the backlog.</summary>
    [Fact]
    public async Task Sends_are_paced_to_the_configured_rate()
    {
        await using var host = Host(("Notifications:SendsPerSecond", "4"));
        var backers = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToList();

        await DecideWithBackers(host, backers);

        // 9 sends at 4 a second: a bucket of one, refilled every 250 ms, needs at least 2 s.
        var sender = host.Services.GetRequiredService<FlakySender>();
        await Scenario.Eventually(async () =>
        {
            await Task.Delay(500, Ct);
            return backers.All(b => Told(host, b) == 1);
        });
        var times = sender.SentAt.ToList();
        Assert.True(times.Max() - times.Min() >= TimeSpan.FromSeconds(1.5), $"9 emails went out in {times.Max() - times.Min()}");
    }
}
