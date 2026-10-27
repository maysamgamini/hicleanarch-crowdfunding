using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;
using Payments.Infrastructure.Psp;

using Npgsql;

namespace IntegrationTests;

/// <summary>
/// A card setup is idempotent per pledge, and a customer per backer: a repeated or racing
/// StartCardSetup, or a retried pledge request, never makes a second setup at the PSP.
/// </summary>
public sealed class CardSetupIdempotencyTests(Database database) : IDisposable
{
    private readonly WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private FakePaymentProvider Psp => _host.Payments().Services.GetRequiredService<FakePaymentProvider>();

    private async Task<CardSetupStarted> Start(Guid pledgeId, Guid backerId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send<StartCardSetup, CardSetupStarted>(new StartCardSetup(pledgeId, backerId), Ct);
        Assert.True(started.IsSuccess, string.Join(", ", started.Errors.Select(e => e.Code)));
        return started.Value;
    }

    [Fact]
    public async Task The_same_setup_sent_twice_keeps_one_row_one_psp_setup_and_the_same_secret()
    {
        var given = new Scenario(database, _host);
        var pledgeId = Guid.NewGuid();
        var backerId = Guid.NewGuid();

        var first = await Start(pledgeId, backerId);
        var again = await Start(pledgeId, backerId);

        Assert.Equal(first, again);
        Assert.Equal(1, Psp.CardSetupsMade);
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.card_setups WHERE pledge_id = @id", pledgeId));
    }

    [Fact]
    public async Task The_same_setup_sent_twice_at_once_keeps_one_row_one_psp_setup_and_the_same_secret()
    {
        var given = new Scenario(database, _host);
        var psp = Psp; // starts the host before the racing calls: the factory must not start it twice
        var pledges = Enumerable.Range(0, 10).Select(_ => (PledgeId: Guid.NewGuid(), BackerId: Guid.NewGuid())).ToArray();

        var twins = await Task.WhenAll(pledges.Select(async p =>
            await Task.WhenAll(Task.Run(() => Start(p.PledgeId, p.BackerId), Ct), Task.Run(() => Start(p.PledgeId, p.BackerId), Ct))));

        Assert.All(twins, pair => Assert.Equal(pair[0], pair[1]));
        Assert.Equal(pledges.Length, psp.CardSetupsMade);
        foreach (var (pledgeId, _) in pledges)
        {
            Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.card_setups WHERE pledge_id = @id", pledgeId));
        }
    }

    [Fact]
    public async Task A_setup_row_from_before_the_keys_takes_the_setup_whose_secret_the_backer_gets()
    {
        var given = new Scenario(database, _host);
        var pledgeId = Guid.NewGuid();
        await given.Execute($"""
            INSERT INTO payments.card_setups (id, pledge_id, backer_id, psp_setup_id, status)
            VALUES ('{Guid.NewGuid()}', @id, '{Guid.NewGuid()}', 'seti_before_the_keys', 'STARTED')
            """, pledgeId);

        var started = await Start(pledgeId, Guid.NewGuid());

        var recorded = await given.Scalar<string>("SELECT psp_setup_id FROM payments.card_setups WHERE pledge_id = @id", pledgeId);
        Assert.StartsWith($"{recorded}_secret_", started.ClientSecret, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backers_first_setups_racing_each_other_make_one_psp_customer()
    {
        var given = new Scenario(database, _host);
        var psp = Psp; // starts the host before the racing calls: the factory must not start it twice
        var backers = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray(); // 24 setups: within the lane's burst of 25

        await Task.WhenAll(backers.SelectMany(b => Enumerable.Range(0, 3).Select(_ => Task.Run(() => Start(Guid.NewGuid(), b), Ct))));

        Assert.Equal(backers.Length, psp.CustomersMade);
        foreach (var backerId in backers)
        {
            Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.psp_customers WHERE backer_id = @id", backerId));
        }
    }

    [Fact]
    public async Task A_retried_pledge_request_with_the_same_key_gets_the_stored_reply_and_starts_no_setup()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var backer = given.Backer(Guid.NewGuid());
        var key = Guid.NewGuid().ToString();

        async Task<HttpResponseMessage> Pledge()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/pledges")
            {
                Content = JsonContent.Create(new { amount_minor = 4_900, tier_id = campaign.TierIds[0], payment_method_id = (string?)null }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await backer.SendAsync(request, Ct);
        }

        var first = await Pledge();
        var retry = await Pledge();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetRawText(),
            (await retry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetRawText());
        Assert.Single(Psp.Calls, c => c.Call == "card-setup");
    }

    [Fact]
    public async Task Asking_again_for_the_card_form_holds_no_pledges_transaction_while_a_slow_psp_answers()
    {
        // This host's Pledges sessions carry their own name, so the test sees only its own. Its card
        // setups may take 10 s: this test is about the Pledges transaction, not the setup's own deadline.
        const string Name = "card-setup-resume-test";
        await using var host = database.StartHost().WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:Pledges", new NpgsqlConnectionStringBuilder(database.Pledges) { ApplicationName = Name }.ConnectionString)
            .UseSetting("Payments:CardSetup:PspDeadline", "00:00:10")
            // The client's own 2 s attempt (s4-l03) would answer first: here it waits as long as the PSP may.
            .UseSetting("Services:Payments:Client:AttemptTimeout", "00:00:15")
            .UseSetting("Services:Payments:Client:TotalTimeout", "00:00:35"));
        var given = new Scenario(database, host);
        var psp = host.Payments().Services.GetRequiredService<FakePaymentProvider>();
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var backer = given.Backer(Guid.NewGuid());
        var placed = await (await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = 4_900, tier_id = campaign.TierIds[0], payment_method_id = (string?)null }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var pledgeId = placed.GetProperty("pledge_id").GetString();
        var secret = placed.GetProperty("setup").GetProperty("client_secret").GetString();
        var key = Guid.NewGuid().ToString();

        Task<HttpResponseMessage> Resume()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/pledges/{pledgeId}/card-setup");
            request.Headers.Add("Idempotency-Key", key);
            return backer.SendAsync(request, Ct);
        }

        var slow = psp.HoldCardSetups();
        var resumed = Resume();
        await slow.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await using (var owner = new NpgsqlConnection(database.Owner))
        {
            await owner.OpenAsync(Ct);
            await using var open = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name AND state LIKE 'idle in transaction%'", owner);
            open.Parameters.AddWithValue("name", Name);
            Assert.Equal(0L, (long)(await open.ExecuteScalarAsync(Ct))!);
        }

        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct); // longer than the Pledges role's 2 s transaction_timeout
        slow.Release();
        var answer = await resumed;
        var retry = await Resume();

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(secret, (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setup").GetProperty("client_secret").GetString());
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(secret, (await retry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setup").GetProperty("client_secret").GetString());
        Assert.Equal(2, psp.Calls.Count(c => c.Call == "card-setup")); // the pledge's and the first resume's; the retry was a replay
        Assert.Equal(1, psp.CardSetupsMade);
    }
}
