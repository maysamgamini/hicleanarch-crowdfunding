using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;
using Payments.Infrastructure.Psp;

namespace IntegrationTests;

/// <summary>
/// Lesson s4-l05: Payments in a host of its own. The monolith runs with <c>Modules:Payments:Mode =
/// Remote</c> and sends the card-setup command over HTTP; the Payments service serves Payments'
/// endpoints, runs its handlers and answers the command.
/// </summary>
public sealed class SplitTests(Database database) : IDisposable
{
    private readonly TwoHosts _hosts = new(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.Dispose();

    private static Guid PledgeId(JsonElement placed) => Guid.ParseExact(placed.GetProperty("pledge_id").GetString()!["plg_".Length..], "N");

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PledgeWithNewCard(HttpClient backer, Scenario.Published campaign)
    {
        var placed = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = 6_000, tier_id = campaign.TierIds[0], payment_method_id = (string?)null }, Ct);
        return (placed, await placed.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    [Fact]
    public async Task A_new_card_pledge_works_end_to_end_with_payments_in_its_own_service()
    {
        var given = new Scenario(database, _hosts.Monolith);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));

        var (response, placed) = await PledgeWithNewCard(given.Backer(Guid.NewGuid()), campaign);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("PENDING_SETUP", placed.GetProperty("status").GetString());
        var secret = placed.GetProperty("setup").GetProperty("client_secret").GetString()!;
        var pledgeId = PledgeId(placed);
        Assert.Equal("STARTED", await given.Scalar<string>("SELECT status FROM payments.card_setups WHERE pledge_id = @id", pledgeId));

        // The backer finishes the card form; the PSP tells the Payments service, never the monolith.
        var webhook = _hosts.Payments.Services.GetRequiredService<FakePaymentProvider>().CompleteSetup(secret[..secret.IndexOf("_secret_", StringComparison.Ordinal)]);
        Assert.True(SplitSwitchTests.NotServed((await Deliver(_hosts.Monolith.CreateClient(), webhook)).StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await Deliver(_hosts.Payments.CreateClient(), webhook)).StatusCode);

        // PaymentMethodSaved crosses back to Pledges, which activates the pledge; CampaignPublished crossed the other way.
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId) == "ACTIVE");
        Assert.Equal(1, await given.Scalar<int>("SELECT claimed FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.campaign_payees WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task With_payments_down_the_card_form_answers_503_with_retry_after_and_saved_card_pledges_carry_on()
    {
        var given = new Scenario(database, _hosts.Monolith);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        _hosts.Link.Down = true;

        // A new card: the pledge keeps its place, without a card form yet, and says when to ask again.
        var backer = given.Backer(Guid.NewGuid());
        var (response, placed) = await PledgeWithNewCard(backer, campaign);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("PENDING_SETUP", placed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, placed.GetProperty("setup").GetProperty("client_secret").ValueKind);
        Assert.True(placed.GetProperty("setup").GetProperty("retry_after_seconds").GetInt32() >= 1);
        Assert.Equal(1, await given.Scalar<int>("SELECT held FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));

        // Asking for the card form while Payments is down: 503, with the wait.
        var pledge = placed.GetProperty("pledge_id").GetString();
        var form = await backer.PostAsync($"/v1/pledges/{pledge}/card-setup", null, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, form.StatusCode);
        Assert.True(form.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        Assert.Equal("payments_unavailable", (await form.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());

        // A saved card never calls Payments: the pledge is made as always.
        var saved = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal("ACTIVE", (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());

        // Payments is back: the same pledge gets its card form.
        _hosts.Link.Down = false;
        var again = await backer.PostAsync($"/v1/pledges/{pledge}/card-setup", null, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.StartsWith("seti_", (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setup").GetProperty("client_secret").GetString(), StringComparison.Ordinal);
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM payments.card_setups WHERE pledge_id = @id", PledgeId(placed)));
    }

    private static Task<HttpResponseMessage> Deliver(HttpClient client, SignedWebhook webhook)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/psp") { Content = new StringContent(webhook.Body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.Header, webhook.Signature);
        return client.SendAsync(request, Ct);
    }
}

/// <summary>Each sub-step of the split is undone by configuration alone.</summary>
public sealed class SplitSwitchTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>No endpoint serves the path here. With deny by default (CQA-910) an unserved path is refused before it is looked up.</summary>
    public static bool NotServed(HttpStatusCode status) => status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized;

    private static async Task<string> CardSetupHandler(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<ICommandHandler<StartCardSetup, CardSetupStarted>>().GetType().Name;
    }

    [Fact]
    public async Task In_process_the_monolith_runs_payments_as_in_release_1()
    {
        using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("Modules:Payments:Calls", "InProcess"));

        Assert.NotEqual("RemoteStartCardSetup", await CardSetupHandler(host));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.CreateClient().PostAsync("/v1/webhooks/psp", new StringContent("{}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Calls_over_http_with_payments_still_here_use_the_monolith_s_own_internal_port()
    {
        // The harness's default since s4-l03: the client goes to this host's own in-memory server.
        using var host = database.StartHost();

        Assert.Equal("RemoteStartCardSetup", await CardSetupHandler(host));
        await using var scope = host.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send<StartCardSetup, CardSetupStarted>(new StartCardSetup(Guid.NewGuid(), Guid.NewGuid()), Ct);
        Assert.True(started.IsSuccess);
    }

    [Fact]
    public async Task Remote_the_monolith_maps_none_of_payments_endpoints_and_sends_the_command_over_http()
    {
        using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("Modules:Payments:Mode", "Remote"));

        Assert.Equal("RemoteStartCardSetup", await CardSetupHandler(host));
        var client = host.CreateClient();
        Assert.True(NotServed((await client.PostAsync("/v1/webhooks/psp", new StringContent("{}"), Ct)).StatusCode));
        Assert.True(NotServed((await client.GetAsync($"/v1/campaigns/cmp_{Guid.NewGuid():N}/settlement", Ct)).StatusCode));
        Assert.True(NotServed((await client.PostAsJsonAsync(PaymentsInternalEndpoints.StartCardSetup, new { pledge_id = Guid.NewGuid(), backer_id = Guid.NewGuid() }, Ct)).StatusCode));
        Assert.DoesNotContain(host.Services.GetServices<BuildingBlocks.Infrastructure.Events.IModuleEvents>(), m => m.Module == PaymentsModuleName.Value);
    }

    [Fact]
    public void Remote_without_http_calls_refuses_to_start()
    {
        using var host = database.StartHost().WithWebHostBuilder(web => web
            .UseSetting("Modules:Payments:Mode", "Remote")
            .UseSetting("Modules:Payments:Calls", "InProcess"));

        var refused = Assert.Throws<InvalidOperationException>(() => host.Services);
        Assert.Contains("Mode = Remote needs Calls = Http", refused.Message, StringComparison.Ordinal);
    }
}

/// <summary>Both hosts run Payments' code during the first sub-step, and the PSP must still see one budget.</summary>
public sealed class SplitBudgetTests(Database database, Valkey valkey) : IClassFixture<Valkey>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_hosts_share_one_psp_budget()
    {
        // One card setup's turn per ten seconds, and the same bucket names in both hosts.
        var prefix = $"psp-split-{Guid.NewGuid():N}";
        void Shared(IWebHostBuilder web) => web
            .UseSetting("ConnectionStrings:Valkey", valkey.Configuration)
            .UseSetting("Payments:Budget:Prefix", prefix)
            .UseSetting("Payments:Budget:CardSetupsPerSecond", "0.1");
        using var monolith = database.StartHost().WithWebHostBuilder(Shared);
        using var payments = TwoHosts.PaymentsService(database, database.Provision().Settings, Shared);
        IServiceProvider[] hosts = [monolith.Services, payments.Services];

        var replies = new List<Result<CardSetupStarted>>();
        for (var i = 0; i < 6; i++)
        {
            await using var scope = hosts[i % 2].CreateAsyncScope();
            replies.Add(await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Send<StartCardSetup, CardSetupStarted>(new StartCardSetup(Guid.NewGuid(), Guid.NewGuid()), Ct));
        }

        // Two hosts with a budget each would have started two; one budget starts one.
        Assert.Equal(1, replies.Count(r => r.IsSuccess));
        Assert.All(replies.Where(r => !r.IsSuccess), r => Assert.Equal("card_setup_lane_full", r.Errors[0].Code));
    }
}

/// <summary>Payments' deliberate back-pressure crosses the network unchanged: the backer keeps the place and gets the wait.</summary>
public sealed class SplitLaneTests(Database database) : IDisposable
{
    // One card setup's turn per ten seconds.
    private readonly TwoHosts _hosts = new(database, web => web.UseSetting("Payments:Budget:CardSetupsPerSecond", "0.1"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.Dispose();

    [Fact]
    public async Task A_full_card_setup_lane_over_http_keeps_the_place_and_answers_429_with_the_wait()
    {
        var given = new Scenario(database, _hosts.Monolith);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        object NewCard() => new { amount_minor = 6_000, tier_id = campaign.TierIds[0], payment_method_id = (string?)null };

        var first = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard(), Ct);
        var backer = given.Backer(Guid.NewGuid());
        var second = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", NewCard(), Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var waiting = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("setup").GetProperty("client_secret").ValueKind);
        Assert.True(waiting.GetProperty("setup").GetProperty("retry_after_seconds").GetInt32() >= 1);
        Assert.Equal(2, await given.Scalar<int>("SELECT held FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));

        var form = await backer.PostAsync($"/v1/pledges/{waiting.GetProperty("pledge_id").GetString()}/card-setup", null, Ct);
        Assert.Equal((HttpStatusCode)429, form.StatusCode);
        Assert.True(form.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        Assert.Equal("card_setup_lane_full", (await form.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }
}
