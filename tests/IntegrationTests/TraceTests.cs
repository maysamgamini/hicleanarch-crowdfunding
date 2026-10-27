using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry.Trace;

using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>
/// Lesson s4-l08, built: a pledge's story followed across the PSP, in the spans an in-memory exporter
/// collects as the collector would, with Payments in a host of its own (both hosts export into one
/// list). A new-card pledge is two traces: A from the click in the monolith to the card setup in
/// Payments (over HTTP), B from the webhook in Payments to the activation in the monolith (through SNS
/// and SQS), and B links to A.
/// </summary>
public sealed class TraceTests : IDisposable
{
    private readonly Database _database;

    private readonly Collected _spans = [];

    private readonly TwoHosts _hosts;

    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

    public TraceTests(Database database)
    {
        _database = database;
        _hosts = new TwoHosts(database, web => web.ConfigureTestServices(s => s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(_spans))));
        _host = _hosts.Monolith;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _hosts.Dispose();

    private FakePaymentProvider Psp => _hosts.Payments.Services.GetRequiredService<FakePaymentProvider>();

    private Task<HttpResponseMessage> Deliver(SignedWebhook webhook)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/psp") { Content = new StringContent(webhook.Body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.Header, webhook.Signature);
        return _hosts.Payments.CreateClient().SendAsync(request, Ct);
    }

    private async Task<Activity> Span(Func<Activity, bool> match)
    {
        await Scenario.Eventually(() => Task.FromResult(_spans.Snapshot().Any(match)));
        return _spans.Snapshot().First(match);
    }

    [Fact]
    public async Task A_new_card_pledge_is_one_trace_to_the_card_setup_and_a_linked_trace_from_the_webhook_to_the_activation()
    {
        var given = new Scenario(_database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));

        // Trace A: the backer's click.
        var a = ActivityTraceId.CreateRandom();
        using var placing = new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/pledges")
        {
            Content = JsonContent.Create(new { amount_minor = 6_000, tier_id = campaign.TierIds[0], payment_method_id = (string?)null }),
        };
        placing.Headers.Add("traceparent", $"00-{a}-{ActivitySpanId.CreateRandom()}-01");
        var placed = await given.Backer(Guid.NewGuid()).SendAsync(placing, Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var body = await placed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var pledgeId = Guid.ParseExact(body.GetProperty("pledge_id").GetString()!["plg_".Length..], "N");
        var secret = body.GetProperty("setup").GetProperty("client_secret").GetString()!;
        var setupId = secret[..secret.IndexOf("_secret_", StringComparison.Ordinal)];

        // The card setup was made inside trace A, by a span of trace A, and remembers it.
        var setupContext = ActivityContext.Parse(await given.Scalar<string>("SELECT trace_parent FROM payments.card_setups WHERE pledge_id = @id", pledgeId), null);
        Assert.Equal(a, setupContext.TraceId);
        var creator = await Span(s => s.SpanId == setupContext.SpanId);
        Assert.Equal(a, creator.TraceId);

        // Trace B: the PSP's webhook, minutes later, with no context of ours.
        Assert.Equal(HttpStatusCode.OK, (await Deliver(Psp.CompleteSetup(setupId))).StatusCode);

        var webhook = await Span(s => s.OperationName == "psp webhook setup_succeeded" && s.Links.Any(l => l.Context.TraceId == a));
        var b = webhook.TraceId;
        Assert.NotEqual(a, b);
        Assert.Equal(setupContext.SpanId, webhook.Links.First(l => l.Context.TraceId == a).Context.SpanId);
        Assert.Equal(a.ToString(), webhook.GetTagItem("crowdfunding.linked_trace_id"));
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId) == "ACTIVE");
        // The activation runs in the monolith, a child of the webhook's trace through the broker (one pledge's story).
        static bool Activation(Activity s) => s.OperationName == "process PaymentMethodSaved" && s.GetTagItem("crowdfunding.handler") is string h && h.StartsWith("Pledges.", StringComparison.Ordinal);
        Assert.Equal(b, (await Span(s => Activation(s) && s.TraceId == b)).TraceId);
        Assert.DoesNotContain(_spans.Snapshot(), s => Activation(s) && s.TraceId == a);
    }

    [Fact]
    public async Task A_charges_webhook_links_to_the_trace_that_created_the_charge()
    {
        var given = new Scenario(_database, _host);
        var campaign = await given.Publish(1_000);
        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = 6_000, tier_id = (string?)null, payment_method_id = FakePaymentProvider.CardSucceeds }, Ct);
        var pledgeId = (await placed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pledge_id").GetString()!;
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaign.CampaignId), Ct);
        }

        // The decision's charge requests reach Payments each in a trace of its own, linked to the decision's.
        await Span(s => s.OperationName == "process PledgeChargeRequested");
        Assert.All(_spans.Snapshot().Where(s => s.OperationName == "process PledgeChargeRequested"),
            s => Assert.Equal("new_trace", s.GetTagItem("crowdfunding.trace_shape")));

        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id AND trace_parent IS NOT NULL", campaign.CampaignId) == 1);
        var created = ActivityContext.Parse(await given.Scalar<string>("SELECT trace_parent FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId), null);

        await Deliver(Psp.Webhook("charge_succeeded", new { pledge_id = pledgeId, payment_id = $"pi_{Guid.NewGuid():N}", attempt_no = 1 }));

        var webhook = await Span(s => s.OperationName == "psp webhook charge_succeeded" && s.Links.Any(l => l.Context.SpanId == created.SpanId));
        Assert.NotEqual(created.TraceId, webhook.TraceId);
        Assert.Equal(created.TraceId.ToString(), webhook.GetTagItem("crowdfunding.linked_trace_id"));
    }

    /// <summary>The exporter adds from the host's threads while a test reads.</summary>
    private sealed class Collected : System.Collections.ObjectModel.Collection<Activity>
    {
        private readonly Lock _lock = new();

        public List<Activity> Snapshot()
        {
            lock (_lock)
            {
                return [.. this];
            }
        }

        protected override void InsertItem(int index, Activity item)
        {
            lock (_lock)
            {
                base.InsertItem(index, item);
            }
        }
    }
}
