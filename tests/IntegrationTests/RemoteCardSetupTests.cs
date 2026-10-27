using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Payments.Client;
using Payments.Infrastructure.Psp;

namespace IntegrationTests;

/// <summary>Lesson s4-l03: the card-setup command over HTTP, to Payments' internal port.</summary>
public sealed class RemoteCardSetupTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A host whose calls to Payments go through <paramref name="wrap"/> on their way to its in-memory server.</summary>
    private WebApplicationFactory<Program> Host(Func<HttpMessageHandler, HttpMessageHandler> wrap) =>
        database.StartHost(services => services.AddHttpClient<RemoteStartCardSetup>()
            .ConfigurePrimaryHttpMessageHandler(sp => wrap(((TestServer)sp.GetRequiredService<IServer>()).CreateHandler())));

    private static object NewCard(long amountMinor) => new { amount_minor = amountMinor, tier_id = (string?)null, payment_method_id = (string?)null };

    /// <summary>Forwards every request; the first answer is held back longer than the client's 2 s attempt.</summary>
    private sealed class FirstAnswerLate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken); // Payments has done the work
            if (Interlocked.Increment(ref _requests) == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken); // the answer is lost on the way back
            }

            return response;
        }
    }

    /// <summary>Counts the requests that reach Payments.</summary>
    private sealed class Counting(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Payments is down: nothing answers on its port.</summary>
    private sealed class Down : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused (localhost:8081)");
    }

    [Fact]
    public async Task A_card_setup_whose_answer_timed_out_is_sent_once_more_and_makes_one_setup()
    {
        FirstAnswerLate? late = null;
        await using var host = Host(server => late = new FirstAnswerLate(server));
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);

        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard(5_000), Ct);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var body = await placed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.NotNull(body.GetProperty("setup").GetProperty("client_secret").GetString());
        Assert.Equal(2, late!.Requests); // the first attempt and one retry, never more
        Assert.Equal(1, await given.Scalar<long>(
            "SELECT count(*) FROM payments.card_setups s JOIN pledges.pledges p ON p.id = s.pledge_id WHERE p.campaign_id = @id",
            campaign.CampaignId));
    }

    [Fact]
    public async Task A_slow_psp_is_answered_409_inside_the_clients_attempt_and_opens_no_breaker()
    {
        Counting? counting = null;
        await using var host = Host(server => counting = new Counting(server))
            .WithWebHostBuilder(web => web.UseSetting("Payments:CardSetup:PspDeadline", "00:00:00.300"));
        var given = new Scenario(database, host);
        var psp = host.Services.GetRequiredService<FakePaymentProvider>();
        var campaign = await given.Publish(1_000);
        psp.HoldCardSetups();

        // More than the breaker's minimum throughput, every one a slow PSP: Payments' own 409, sent once each.
        for (var i = 0; i < 12; i++)
        {
            var clock = Stopwatch.StartNew();
            var refused = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard(5_000), Ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("card_setup_unavailable", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"answered after {clock.Elapsed}");
        }

        Assert.Equal(12, counting!.Requests);

        // A 409 is Payments' answer, not a failure of the call: the client's breaker is closed, so the next one reaches Payments.
        await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard(5_000), Ct);
        Assert.Equal(13, counting.Requests);
    }

    [Fact]
    public async Task With_payments_down_a_saved_card_pledges_as_before_and_a_new_card_pledge_keeps_its_place()
    {
        await using var host = Host(_ => new Down());
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);

        var saved = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(5_000), Ct);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        var backer = given.Backer(Guid.NewGuid());
        var newCard = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard(4_000), Ct);
        Assert.Equal(HttpStatusCode.Created, newCard.StatusCode);
        var body = await newCard.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("setup").GetProperty("client_secret").ValueKind);
        Assert.Equal(5, body.GetProperty("setup").GetProperty("retry_after_seconds").GetInt32());

        var resumed = await backer.PostAsync($"/v1/pledges/{body.GetProperty("pledge_id").GetString()}/card-setup", null, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resumed.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), resumed.Headers.RetryAfter!.Delta);
        Assert.Equal("payments_unavailable", (await resumed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_internal_endpoint_does_not_exist_on_the_public_port()
    {
        // The in-memory server's connections arrive on port 0, which is not the internal port here.
        await using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("Payments:Internal:Port", "8081"));

        var answer = await host.CreateClient().PostAsJsonAsync(Payments.Contracts.PaymentsInternalEndpoints.StartCardSetup,
            new { pledge_id = Guid.NewGuid(), backer_id = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }
}
