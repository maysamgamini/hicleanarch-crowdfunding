using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Payments.Application;
using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>Lesson s3-l09, built: 7 days to recover a failed card, then the charge is dropped and the campaign stays funded.</summary>
public sealed class DunningTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private FakePaymentProvider Psp => _host.Payments().Services.GetRequiredService<FakePaymentProvider>();

    private async Task<T> Send<TCommand, T>(TCommand command)
        where TCommand : ICommand<T>
    {
        await using var scope = _host.ServicesFor<TCommand>().CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<TCommand, T>(command, Ct)).Value;
    }

    private Task<bool> Settle(Scenario.Published campaign) => Send<RunSettlement, bool>(new RunSettlement("dunning", campaign.CampaignId));

    /// <summary>A funded campaign with one pledge on <paramref name="card"/>, decided, its charge tried once.</summary>
    private async Task<(Scenario Given, Scenario.Published Campaign, Guid BackerId, string PledgeId)> FailedOnce(string card)
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(1_000);
        var backerId = Guid.NewGuid();
        var placed = await given.Backer(backerId).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = 6_000, tier_id = (string?)null, payment_method_id = card }, Ct);
        var pledgeId = (await placed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pledge_id").GetString()!;
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaign.CampaignId));
        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) == 1
            && await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId) == 1);
        await Settle(campaign);
        Assert.Equal("PAYMENT_FAILED", await ChargeStatus(given, campaign));
        return (given, campaign, backerId, pledgeId);
    }

    private static Task<string> ChargeStatus(Scenario given, Scenario.Published campaign) =>
        given.Scalar<string>("SELECT status FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId);

    private static Task<string> PledgeStatus(Scenario given, Scenario.Published campaign) =>
        given.Scalar<string>("SELECT status FROM pledges.pledges WHERE campaign_id = @id", campaign.CampaignId);

    private Task<HttpResponseMessage> Deliver(SignedWebhook webhook)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/psp") { Content = new StringContent(webhook.Body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.Header, webhook.Signature);
        return Database.Client(_host).SendAsync(request, Ct);
    }

    [Fact]
    public async Task A_soft_decline_is_retried_on_days_one_three_five_and_seven_then_dropped_and_the_campaign_stays_funded()
    {
        var (given, campaign, _, _) = await FailedOnce(FakePaymentProvider.CardDeclined);

        foreach (var day in new[] { 1, 3, 5, 7 })
        {
            // Time passes: the window opened `day` days ago, and the next attempt is due.
            await given.Execute($"UPDATE payments.charges SET payment_failed_at = now() - interval '{day} days', next_attempt_at = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
            await Settle(campaign);
        }

        Assert.Equal("DROPPED", await ChargeStatus(given, campaign));
        Assert.Equal(5L, await given.Scalar<long>(
            "SELECT count(DISTINCT a.idempotency_key) FROM payments.charge_attempts a JOIN payments.charges c USING (pledge_id) WHERE c.campaign_id = @id AND a.status = 'FAILED'", campaign.CampaignId));
        Assert.Equal("DONE", await given.Scalar<string>("SELECT status FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId));

        await Scenario.Eventually(async () => await PledgeStatus(given, campaign) == "DROPPED");
        Assert.Equal("FUNDED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(-6_000L, await given.Scalar<long>("SELECT delta_minor FROM pledges.pledge_ledger WHERE campaign_id = @id AND kind = 'DROP'", campaign.CampaignId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT final_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_card_the_issuer_says_never_to_try_again_waits_for_the_backer_then_is_dropped_without_another_attempt()
    {
        var (given, campaign, _, _) = await FailedOnce(FakePaymentProvider.CardFraudulent);
        Assert.Equal("do_not_try_again", await given.Scalar<string>("SELECT last_advice_code FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));
        Assert.True(await given.Scalar<bool>("SELECT next_attempt_at > now() + interval '6 days' FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));
        var charges = Psp.ChargesMade;

        // The window ends, and the backer never gave another card.
        await given.Execute("UPDATE payments.charges SET payment_failed_at = now() - interval '7 days', next_attempt_at = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await Settle(campaign);

        Assert.Equal("DROPPED", await ChargeStatus(given, campaign));
        Assert.Equal(charges, Psp.ChargesMade);
        Assert.Equal(1L, await given.Scalar<long>(
            "SELECT count(*) FROM payments.charge_attempts a JOIN payments.charges c USING (pledge_id) WHERE c.campaign_id = @id", campaign.CampaignId));
        await Scenario.Eventually(async () => await PledgeStatus(given, campaign) == "DROPPED");
    }

    [Fact]
    public async Task A_decline_the_PSP_advises_to_try_again_later_is_retried_on_its_own_the_next_day()
    {
        var (given, campaign, _, _) = await FailedOnce(FakePaymentProvider.CardTryAgainLater);

        Assert.True(await given.Scalar<bool>(
            "SELECT next_attempt_at BETWEEN now() + interval '23 hours' AND now() + interval '25 hours' FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task After_a_hard_decline_a_new_card_from_the_backer_is_tried_at_once()
    {
        var (given, campaign, backerId, pledgeId) = await FailedOnce(FakePaymentProvider.CardExpired);
        Assert.True(await given.Scalar<bool>("SELECT next_attempt_at > now() + interval '6 days' FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));

        var changed = await given.Backer(backerId).PutAsJsonAsync($"/v1/pledges/{pledgeId}/payment-method", new { payment_method_id = FakePaymentProvider.CardSucceeds }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        await Settle(campaign);

        Assert.Equal("CHARGED", await ChargeStatus(given, campaign));
        await Scenario.Eventually(async () => await PledgeStatus(given, campaign) == "CHARGED");
    }

    [Fact]
    public async Task When_the_bank_wants_the_cardholder_the_backer_confirms_on_our_page_and_is_charged_once()
    {
        var (given, campaign, backerId, pledgeId) = await FailedOnce(FakePaymentProvider.CardNeedsAuthentication);
        var backer = given.Backer(backerId);
        var before = Psp.ChargesMade;

        HttpRequestMessage Confirm()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/pledges/{pledgeId}/confirm-payment");
            request.Headers.Add("Idempotency-Key", "confirm-1");
            return request;
        }

        var confirmed = await backer.SendAsync(Confirm(), Ct);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var payment = await confirmed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var paymentId = payment.GetProperty("psp_payment_id").GetString()!;
        Assert.NotNull(payment.GetProperty("client_secret").GetString());

        var again = await backer.SendAsync(Confirm(), Ct); // the browser retried: the same payment, not a new attempt
        Assert.Equal(paymentId, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("psp_payment_id").GetString());
        Assert.Equal(2, await given.Scalar<int>("SELECT attempt_no FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId));

        // The backer confirms in the browser; the PSP says so.
        (await Deliver(Psp.FinishOnSessionPayment(paymentId))).EnsureSuccessStatusCode();

        Assert.Equal("CHARGED", await ChargeStatus(given, campaign));
        Assert.Equal(before + 1, Psp.ChargesMade);
        await Scenario.Eventually(async () => await PledgeStatus(given, campaign) == "CHARGED");
    }

    [Fact]
    public async Task A_confirmation_still_waiting_when_the_window_ends_is_cancelled_and_the_charge_dropped()
    {
        var (given, campaign, backerId, pledgeId) = await FailedOnce(FakePaymentProvider.CardNeedsAuthentication);
        (await given.Backer(backerId).PostAsync($"/v1/pledges/{pledgeId}/confirm-payment", null, Ct)).EnsureSuccessStatusCode();

        await given.Execute("UPDATE payments.charges SET payment_failed_at = now() - interval '8 days' WHERE campaign_id = @id", campaign.CampaignId);
        await Settle(campaign);

        Assert.Equal("DROPPED", await ChargeStatus(given, campaign));
        await Scenario.Eventually(async () => await PledgeStatus(given, campaign) == "DROPPED");
    }

    [Fact]
    public async Task Only_the_backer_can_act_and_only_for_what_the_decline_asks()
    {
        var (given, _, backerId, pledgeId) = await FailedOnce(FakePaymentProvider.CardDeclined);

        var someoneElse = await given.Backer(Guid.NewGuid()).PostAsync($"/v1/pledges/{pledgeId}/confirm-payment", null, Ct);
        var notAsked = await given.Backer(backerId).PostAsync($"/v1/pledges/{pledgeId}/confirm-payment", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, someoneElse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, notAsked.StatusCode);
        Assert.Equal("charge_not_awaiting_backer", (await notAsked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }
}
