using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Payments.Client;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l12: when another service fails. Pledges' one synchronous call, the card setup to Payments,
/// and its link to the broker are broken on purpose, through Toxiproxy (<see cref="Faults"/>): Payments
/// slow, down, resetting connections, hanging or answering 503, and a broker this host cannot reach.
/// </summary>
public sealed class FailureTests(Database database, Faults faults) : IClassFixture<Faults>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object NewCard => new { amount_minor = 5_000, tier_id = (string?)null, payment_method_id = (string?)null };

    public async ValueTask InitializeAsync()
    {
        await faults.Reset();
        faults.Payments.Count();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A Pledges host whose card setups go over TCP through Toxiproxy to the Payments service beside it.</summary>
    private WebApplicationFactory<Program> Host(IReadOnlyDictionary<string, string?>? broker = null, Action<IWebHostBuilder>? configure = null)
    {
        var host = (broker is null ? database.StartHost() : database.StartHost(broker, recoverAfter: "00:00:01",
            // Only Pledges recovers quickly: every test shares the database, and a service beside this host that
            // recovered every second would publish other tests' rows through this test's broker.
            payments: Patient, campaigns: Patient)).WithWebHostBuilder(web =>
        {
            web.UseSetting("Services:Payments:BaseAddress", faults.Address("payments").ToString());
            web.ConfigureServices(s => s.AddHttpClient<RemoteStartCardSetup>().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler()));
            configure?.Invoke(web);
        });
        faults.Payments.Target = new HttpMessageInvoker(PaymentsBeside.Link(
            host.Services.GetRequiredService<TwoHosts.PaymentsLink>(), host.Services.GetRequiredService<PaymentsBeside>()), disposeHandler: false);
        return host;
    }

    private static void Patient(IWebHostBuilder web) => web.UseSetting("Outbox:Publishing:RecoverAfter", "00:10:00");

    [Fact]
    public async Task With_payments_slow_a_task_holds_no_more_card_setups_than_its_limit_and_the_rest_keep_their_place_at_once()
    {
        await using var host = Host(configure: web => web.UseSetting("Services:Payments:Client:MaxConcurrentCalls", "4"));
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        await faults.Slow("payments", TimeSpan.FromSeconds(1.5)); // slow, but inside the 2 s attempt: no timeout fires
        faults.Payments.Count();

        var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            var clock = Stopwatch.StartNew();
            var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard, Ct);
            return (placed.StatusCode, Body: await placed.Content.ReadFromJsonAsync<JsonElement>(Ct), clock.Elapsed);
        }));

        Assert.InRange(faults.Payments.MostInFlight, 1, 4);
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Created, a.StatusCode)); // every pledge keeps its place
        var withForm = answers.Where(a => a.Body.GetProperty("setup").GetProperty("client_secret").ValueKind == JsonValueKind.String).ToList();
        var turnedAway = answers.Except(withForm).ToList();
        Assert.InRange(withForm.Count, 1, 4);
        Assert.Equal(faults.Payments.Requests, withForm.Count); // the limiter's refusals never reached Payments
        Assert.All(turnedAway, a =>
        {
            Assert.Equal(5, a.Body.GetProperty("setup").GetProperty("retry_after_seconds").GetInt32());
            Assert.True(a.Elapsed < TimeSpan.FromSeconds(1.5), $"turned away after {a.Elapsed}, not at once");
        });
    }

    [Fact]
    public async Task With_payments_down_saved_card_pledges_work_and_the_card_form_answers_503_with_retry_after()
    {
        await using var host = Host();
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        await faults.Down("payments");

        var saved = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(5_000), Ct);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        var backer = given.Backer(Guid.NewGuid());
        var placed = await (await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.Null, placed.GetProperty("setup").GetProperty("client_secret").ValueKind);

        var form = await backer.PostAsync($"/v1/pledges/{placed.GetProperty("pledge_id").GetString()}/card-setup", null, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, form.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), form.Headers.RetryAfter!.Delta);
        Assert.Equal("payments_unavailable", (await form.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task With_connections_reset_the_card_setup_is_answered_inside_the_5_s_budget_and_the_pledge_keeps_its_place()
    {
        await using var host = Host();
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        await faults.ResetConnections("payments");

        var clock = Stopwatch.StartNew();
        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard, Ct);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"answered after {clock.Elapsed}");
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        Assert.Equal(5, (await placed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setup").GetProperty("retry_after_seconds").GetInt32());
    }

    [Fact]
    public async Task A_payments_that_never_answers_is_cut_at_2_s_an_attempt_and_answered_inside_5_s()
    {
        await using var host = Host();
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        await faults.Hang("payments");

        var clock = Stopwatch.StartNew();
        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard, Ct);

        // Two attempts of 2 s and the wait between them: past one attempt, inside the total.
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(3.9), TimeSpan.FromSeconds(5.5));
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        Assert.Equal(5, (await placed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setup").GetProperty("retry_after_seconds").GetInt32());
    }

    [Fact]
    public async Task A_503_with_retry_after_is_tried_once_more_and_its_wait_is_passed_to_the_backer()
    {
        await using var host = Host();
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        var backer = given.Backer(Guid.NewGuid());
        var placed = await (await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        faults.Payments.Answer = PaymentsSocket.Unavailable(retryAfterSeconds: 7);
        faults.Payments.Count();

        var form = await backer.PostAsync($"/v1/pledges/{placed.GetProperty("pledge_id").GetString()}/card-setup", null, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, form.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), form.Headers.RetryAfter!.Delta);
        Assert.Equal(2, faults.Payments.Requests); // the first attempt and one retry: the pledge id is the Idempotency-Key
    }

    [Fact]
    public async Task With_the_broker_unreachable_pledges_still_commit_and_their_events_are_published_once_it_is_back()
    {
        await faults.Broker(database.Broker.NetworkAddress);
        var proxied = faults.Address("broker").ToString().TrimEnd('/');
        var broker = database.Provision().Settings.ToDictionary(kv => kv.Key, kv => kv.Value?.Replace(database.Broker.ServiceUrl, proxied, StringComparison.Ordinal));
        await using var host = Host(broker); // a recovery pass every second
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        var since = DateTimeOffset.UtcNow;

        await faults.Down("broker");
        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(5_000), Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode); // the pledge commits: the broker is not on its path
        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // the publish after the commit, and a recovery pass, fail
        var owed = await Unpublished(campaign.CampaignId, since);
        Assert.True(owed > 0, "the pledge's events are kept in the outbox while the broker is unreachable");

        await faults.Up("broker");
        var clock = Stopwatch.StartNew();
        while (await Unpublished(campaign.CampaignId, since) > 0)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "the outbox did not publish once the broker was back");
            await Task.Delay(500, Ct);
        }
    }

    /// <summary>Rows of Pledges' outbox about the campaign, written since <paramref name="since"/>, still owed to the broker.</summary>
    private async Task<long> Unpublished(Guid campaignId, DateTimeOffset since)
    {
        await using var connection = new NpgsqlConnection(database.Owner);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pledges.outbox
            WHERE published_at IS NULL AND 'broker' = ANY (sinks) AND occurred_at >= @since AND payload->>'campaignId' = @id
            """, connection);
        command.Parameters.AddWithValue("since", since);
        command.Parameters.AddWithValue("id", campaignId.ToString());
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
