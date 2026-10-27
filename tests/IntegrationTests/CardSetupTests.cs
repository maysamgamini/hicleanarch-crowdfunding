using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>Lesson 1's second step, built: a new card is saved at the PSP, and its webhook activates the pledge.</summary>
public sealed class CardSetupTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private FakePaymentProvider Psp => _host.Services.GetRequiredService<FakePaymentProvider>();

    /// <summary>A pledge without a saved card: its id, and the PSP's setup id, which starts its client secret.</summary>
    private static async Task<(Guid PledgeId, string SetupId, JsonElement Body)> PledgeWithNewCard(HttpClient backer, Scenario.Published campaign, long amount = 6_000, int tier = 0)
    {
        var placed = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = amount, tier_id = campaign.TierIds[tier], payment_method_id = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var body = await placed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secret = body.GetProperty("setup").GetProperty("client_secret").GetString()!;
        return (Guid.ParseExact(body.GetProperty("pledge_id").GetString()!["plg_".Length..], "N"), secret[..secret.IndexOf("_secret_", StringComparison.Ordinal)], body);
    }

    private Task<HttpResponseMessage> Deliver(SignedWebhook webhook)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/psp") { Content = new StringContent(webhook.Body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.Header, webhook.Signature);
        return _host.CreateClient().SendAsync(request, Ct);
    }

    private async Task ExpireHolds()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<ExpireHolds, int>(new ExpireHolds(), Ct);
    }

    [Fact]
    public async Task A_new_card_pledge_waits_holds_a_place_and_returns_the_card_forms_secret()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));

        var (pledgeId, setupId, body) = await PledgeWithNewCard(given.Backer(Guid.NewGuid()), campaign);

        Assert.Equal("PENDING_SETUP", body.GetProperty("status").GetString());
        Assert.Equal("psp", body.GetProperty("setup").GetProperty("provider").GetString());
        var expires = body.GetProperty("hold_expires_at").GetDateTimeOffset();
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
        Assert.Equal(1, await given.Scalar<int>("SELECT held FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal("STARTED", await given.Scalar<string>("SELECT status FROM payments.card_setups WHERE pledge_id = @id", pledgeId));
        Assert.StartsWith("seti_", setupId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_signed_webhook_activates_the_pledge_and_it_counts()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var (pledgeId, setupId, _) = await PledgeWithNewCard(given.Backer(Guid.NewGuid()), campaign);
        var webhook = Psp.CompleteSetup(setupId);

        var received = await Deliver(webhook);

        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId) == "ACTIVE");
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1, await given.Scalar<int>("SELECT claimed FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(0, await given.Scalar<int>("SELECT held FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        var messageId = JsonDocument.Parse(webhook.Body).RootElement.GetProperty("id").GetString();
        Assert.Equal($"psp:{messageId}", await given.Scalar<string>("SELECT cause_id FROM pledges.pledge_ledger WHERE pledge_id = @id", pledgeId));
        Assert.Equal(FakePaymentProvider.CardSucceeds, await given.Scalar<string>("SELECT payment_method_ref FROM pledges.pledges WHERE id = @id", pledgeId));
    }

    [Fact]
    public async Task A_forged_or_stale_webhook_is_refused_and_changes_nothing()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var (pledgeId, setupId, _) = await PledgeWithNewCard(given.Backer(Guid.NewGuid()), campaign);
        var genuine = Psp.CompleteSetup(setupId);

        var forged = await Deliver(genuine with { Signature = WebhookSignature.Sign("not-the-secret-0123456789", genuine.Body, DateTimeOffset.UtcNow) });
        var tampered = await Deliver(genuine with { Body = genuine.Body.Replace("pm_card_visa", "pm_card_other", StringComparison.Ordinal) });
        var stale = await Deliver(genuine with { Signature = WebhookSignature.Sign(Database.WebhookSecret, genuine.Body, DateTimeOffset.UtcNow.AddMinutes(-10)) });

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal("STARTED", await given.Scalar<string>("SELECT status FROM payments.card_setups WHERE pledge_id = @id", pledgeId));
        Assert.Equal("PENDING_SETUP", await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId));
    }

    [Fact]
    public async Task The_same_webhook_twice_activates_once()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var (pledgeId, setupId, _) = await PledgeWithNewCard(given.Backer(Guid.NewGuid()), campaign);
        var webhook = Psp.CompleteSetup(setupId);

        var first = await Deliver(webhook);
        var second = await Deliver(webhook);

        Assert.Equal("applied", (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("received").GetString());
        Assert.Equal("duplicate", (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("received").GetString());
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId) == "ACTIVE");
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE pledge_id = @id", pledgeId));
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task The_sweeper_expires_a_hold_and_gives_the_place_back()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 1));
        var (pledgeId, _, _) = await PledgeWithNewCard(given.Backer(Guid.NewGuid()), campaign);
        var someoneElse = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct);
        Assert.Equal(HttpStatusCode.Conflict, someoneElse.StatusCode); // the only place is held

        await given.Execute("UPDATE pledges.pledges SET hold_expires_at = now() - interval '1 second' WHERE id = @id", pledgeId);
        await ExpireHolds();

        Assert.Equal("EXPIRED", await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId));
        Assert.Equal(0, await given.Scalar<int>("SELECT held FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        var now = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(4_900, campaign.TierIds[0]), Ct);
        Assert.Equal(HttpStatusCode.Created, now.StatusCode);
    }

    [Fact]
    public async Task A_late_card_never_revives_an_expired_pledge()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var (pledgeId, setupId, _) = await PledgeWithNewCard(given.Backer(Guid.NewGuid()), campaign);
        await given.Execute("UPDATE pledges.pledges SET hold_expires_at = now() - interval '1 second' WHERE id = @id", pledgeId);
        await ExpireHolds();

        var late = await Deliver(Psp.CompleteSetup(setupId));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode); // the PSP is told we got it
        await Scenario.Eventually(async () => await given.Scalar<long>("SELECT count(*) FROM pledges.inbox WHERE handler LIKE '%ActivatePledge'", Guid.Empty) > 0
            && await given.Scalar<long>("SELECT count(*) FROM payments.outbox WHERE delivered_at IS NULL", Guid.Empty) == 0);
        Assert.Equal("EXPIRED", await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(0, await given.Scalar<int>("SELECT claimed + held FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_card_saved_after_the_campaign_closed_does_not_count()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var (pledgeId, setupId, _) = await PledgeWithNewCard(given.Backer(Guid.NewGuid()), campaign);
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);

        await Deliver(Psp.CompleteSetup(setupId));

        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId) == "EXPIRED");
        Assert.Equal(0L, await given.Scalar<long>("SELECT pledged_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(0, await given.Scalar<int>("SELECT claimed + held FROM pledges.tier_counters WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE pledge_id = @id", pledgeId));
    }

    [Fact]
    public void A_signature_is_checked_over_the_raw_body_and_its_time()
    {
        var now = DateTimeOffset.UtcNow;
        var signature = WebhookSignature.Sign("secret-0123456789abcdef", "{\"a\":1}", now);

        Assert.True(WebhookSignature.Verify("secret-0123456789abcdef", signature, "{\"a\":1}", now.AddMinutes(4)));
        Assert.False(WebhookSignature.Verify("secret-0123456789abcdef", signature, "{\"a\": 1}", now));
        Assert.False(WebhookSignature.Verify("secret-0123456789abcdef", signature, "{\"a\":1}", now.AddMinutes(6)));
        Assert.False(WebhookSignature.Verify("secret-0123456789abcdef", null, "{\"a\":1}", now));
        Assert.False(WebhookSignature.Verify("secret-0123456789abcdef", "t=abc,v1=00", "{\"a\":1}", now));
    }
}
