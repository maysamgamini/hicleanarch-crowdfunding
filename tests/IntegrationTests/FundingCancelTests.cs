using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;

using Campaigns.Contracts;
using Campaigns.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Pledges.Application;
using Pledges.Infrastructure.FundingCancel;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l05: the creator's LIVE cancel goes to Pledges, which owns the deadline, on
/// <c>POST /v1/campaigns/{id}/funding/cancel</c>; drafts stay with Campaigns; the old path's LIVE
/// branch still works, marked deprecated.
/// </summary>
public sealed class FundingCancelTests(Database database) : IDisposable
{
    private readonly WebApplicationFactory<Program> _host = database.StartHost()
        .WithWebHostBuilder(web => web.UseSetting("Pledges:FundingCancel:Open", "true"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private static Task<HttpResponseMessage> CancelFunding(HttpClient creator, string campaignId) =>
        creator.PostAsync($"/v1/campaigns/{campaignId}/funding/cancel", null, Ct);

    private async Task<string> CatalogStatus(string campaignId) =>
        (await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaignId}", Ct)).GetProperty("status").GetString()!;

    [Fact]
    public async Task A_creators_live_cancel_is_answered_by_Pledges_and_the_catalog_follows()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();

        var cancelled = await CancelFunding(given.Creator(), campaign.Id);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("CANCELLED", (await cancelled.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        var route = Assert.Single(MappedEndpoints.Of(_host), e => e.Name == "POST /v1/campaigns/{id}/funding/cancel").Endpoint;
        Assert.Contains("Pledges", route.Metadata.GetMetadata<ITagsMetadata>()!.Tags);
        Assert.Equal("CANCELLED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        // Campaigns hears it from FundingCancelled, as after any accepted cancel.
        await Scenario.Eventually(async () => await CatalogStatus(campaign.Id) == "CANCELLED");
        Assert.False(cancelled.Headers.Contains("Deprecation"));
    }

    [Fact]
    public async Task The_funding_cancel_is_not_served_until_it_is_opened()
    {
        using var closed = database.StartHost();
        var given = new Scenario(database, closed);
        var campaign = await given.Publish();

        var response = await CancelFunding(given.Creator(), campaign.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LIVE", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_lost_reply_is_reconciled_by_sending_the_same_key_again_and_the_cancel_happens_once()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var creator = given.Creator();
        var key = Guid.NewGuid().ToString();

        // The first reply never reaches the creator: the cancel happened all the same.
        using (var lost = new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/funding/cancel"))
        {
            lost.Headers.Add("Idempotency-Key", key);
            (await creator.SendAsync(lost, Ct)).Dispose();
        }

        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/funding/cancel");
        retry.Headers.Add("Idempotency-Key", key);
        var retried = await creator.SendAsync(retry, Ct);
        var again = await CancelFunding(creator, campaign.Id);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("CANCELLED", (await retried.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1L, await given.Scalar<long>(
            "SELECT count(*) FROM pledges.outbox WHERE type = 'FundingCancelled' AND payload::jsonb ->> 'campaignId' = @id::text", campaign.CampaignId));
        await Scenario.Eventually(async () => await CatalogStatus(campaign.Id) == "CANCELLED");
    }

    [Fact]
    public async Task Another_creator_is_refused()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var someoneElse = Database.Client(_host, Database.Token(Roles.Creator));

        var response = await CancelFunding(someoneElse, campaign.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("LIVE", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_cancel_after_the_deadline_is_refused()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);

        var response = await CancelFunding(given.Creator(), campaign.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("campaign_closed", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal("LIVE", await CatalogStatus(campaign.Id));
    }

    [Fact]
    public async Task A_draft_is_cancelled_by_Campaigns_on_its_own_path_and_Pledges_has_no_window_for_it()
    {
        var given = new Scenario(database, _host);
        var draft = await given.Draft();

        var atPledges = await CancelFunding(given.Creator(), draft);
        var atCampaigns = await given.Creator().PostAsync($"/v1/campaigns/{draft}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, atPledges.StatusCode);
        Assert.Equal(HttpStatusCode.OK, atCampaigns.StatusCode);
        Assert.False(atCampaigns.Headers.Contains("Deprecation"));
        Assert.Equal("CANCELLED", await CatalogStatus(draft));
    }

    [Fact]
    public async Task A_window_opened_before_the_change_is_refused_until_the_backfill_gives_it_its_creator()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET creator_id = NULL WHERE campaign_id = @id", campaign.CampaignId);

        var before = await CancelFunding(given.Creator(), campaign.Id);
        var missing = await _host.Services.CheckCreators(Ct);
        await _host.Services.RecordCreators(Ct);
        await Scenario.Eventually(async () => !await given.Scalar<bool>("SELECT creator_id IS NULL FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        await Scenario.Eventually(async () => await _host.Services.CheckCreators(Ct) == 0);
        var someoneElse = await CancelFunding(Database.Client(_host, Database.Token(Roles.Creator)), campaign.Id);
        var after = await CancelFunding(given.Creator(), campaign.Id);

        Assert.Equal(HttpStatusCode.Conflict, before.StatusCode);
        Assert.Equal("creator_unknown", (await before.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal(1, missing);
        Assert.Equal(HttpStatusCode.Forbidden, someoneElse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_stored_creator_is_never_overwritten_by_a_different_one()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RecordCreator>()
                .Handle(new CampaignCreatorRecorded(Guid.NewGuid(), DateTimeOffset.UtcNow, campaign.CampaignId, Guid.NewGuid()), Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await CancelFunding(given.Creator(), campaign.Id)).StatusCode);
    }

    [Fact]
    public async Task The_old_path_still_cancels_a_live_campaign_and_says_it_is_deprecated()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();

        var response = await given.Creator().PostAsync($"/v1/campaigns/{campaign.Id}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("@" + new LiveCancelOptions().DeprecatedAt.ToUnixTimeSeconds(), Assert.Single(response.Headers.GetValues("Deprecation")));
        Assert.Equal("Mon, 01 Feb 2027 00:00:00 GMT", Assert.Single(response.Headers.GetValues("Sunset")));
        Assert.Contains($"</v1/campaigns/{campaign.Id}/funding/cancel>; rel=\"successor-version\"", response.Headers.GetValues("Link"));
        Assert.Equal("CANCELLED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal("CANCELLED", await CatalogStatus(campaign.Id));
    }
}
