using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

/// <summary>
/// Lesson s5-l08's routes: Campaigns' paths go to the Campaigns service, ahead of the catch-all; Pledges'
/// paths under /v1/campaigns/{id} (pledges, totals, the funding cancel) stay with the monolith, and so does
/// every other method on Campaigns' paths.
/// </summary>
public sealed class CampaignsRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> Start(EchoService monolith, EchoService campaigns) =>
        GatewayHost.Start(monolith.Address).WithWebHostBuilder(web => web
            .UseSetting("ReverseProxy:Clusters:campaigns:Destinations:campaigns:Address", campaigns.Address));

    public static TheoryData<string, string, string> Paths() => new()
    {
        { "POST", "/v1/campaigns", "campaigns" },
        { "GET", "/v1/campaigns/cmp_1", "campaigns" },
        { "POST", "/v1/campaigns/cmp_1/publish", "campaigns" },
        { "POST", "/v1/campaigns/cmp_1/cancel", "campaigns" },
        { "POST", "/v1/campaigns/cmp_1/pledges", "pledges" },
        { "GET", "/v1/campaigns/cmp_1/totals", "pledges" },
        { "POST", "/v1/campaigns/cmp_1/funding/cancel", "pledges" },
        { "DELETE", "/v1/campaigns/cmp_1", "pledges" },
        { "GET", "/v1/pledges/plg_1", "pledges" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Campaigns_paths_reach_campaigns_and_Pledges_paths_the_monolith(string method, string path, string service)
    {
        await using var monolith = await EchoService.Start("pledges");
        await using var campaigns = await EchoService.Start("campaigns");
        await using var gateway = Start(monolith, campaigns);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method is "GET" or "DELETE" ? null : new StringContent("{}") };
        request.Headers.Add("X-Forwarded-For", "203.0.113.22");

        var echo = await (await gateway.CreateClient().SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(service, echo.GetProperty("service").GetString());
    }
}
