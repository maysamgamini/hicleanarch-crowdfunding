using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using CrowdFunding.Gateway;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

/// <summary>
/// Lesson s4-l05's routes: Payments' four public paths go to the Payments service, ahead of the
/// catch-all, and everything else still goes to the monolith. The PSP's webhook is limited for the
/// whole route, not per address (CQA-1306).
/// </summary>
public sealed class PaymentsRouteTests(Valkey valkey) : IClassFixture<Valkey>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The class's Valkey with a database of the test's own. The webhook route has one bucket for every
    /// gateway task (gateway:route:webhook), so a test that drains it would otherwise leave it empty for
    /// whichever test runs next.
    /// </summary>
    private string OwnBuckets(int database) => $"{valkey.Configuration},defaultDatabase={database}";

    private static WebApplicationFactory<Program> Start(EchoService monolith, EchoService payments, string valkey = "",
        int perSecond = 20, int burst = 40, double webhookPerSecond = 1000, int webhookBurst = 1000, string pspSources = PspSources) =>
        GatewayHost.Start(monolith.Address, valkey, perSecond, burst).WithWebHostBuilder(web => web
            .UseSetting("ReverseProxy:Clusters:payments:Destinations:payments:Address", payments.Address)
            .UseSetting("EdgeLimits:Webhook:Sources", pspSources)
            .UseSetting("EdgeLimits:Webhook:PerSecond", webhookPerSecond.ToString(CultureInfo.InvariantCulture))
            .UseSetting("EdgeLimits:Webhook:Burst", webhookBurst.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The PSP's published webhook addresses in these tests: 198.51.100.0/24 (TEST-NET-2).</summary>
    private const string PspSources = "198.51.100.0/24";

    private static HttpRequestMessage Webhook(string clientIp, string body = "{}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/psp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Forwarded-For", clientIp);
        request.Headers.Add("Psp-Signature", "t=1,v1=abc");
        return request;
    }

    public static TheoryData<string, string, string> Paths() => new()
    {
        { "POST", "/v1/webhooks/psp", "payments" },
        { "PUT", "/v1/pledges/plg_1/payment-method", "payments" },
        { "POST", "/v1/pledges/plg_1/confirm-payment", "payments" },
        { "GET", "/v1/campaigns/cmp_1/settlement", "payments" },
        // Pledges' and Campaigns' own paths, including the ones next to Payments' and other methods on Payments' paths.
        { "GET", "/v1/pledges/plg_1", "monolith" },
        { "PATCH", "/v1/pledges/plg_1", "monolith" },
        { "POST", "/v1/pledges/plg_1/card-setup", "monolith" },
        { "GET", "/v1/pledges/plg_1/payment-method", "monolith" },
        { "GET", "/v1/campaigns/cmp_1", "monolith" },
        { "GET", "/v1/campaigns/cmp_1/totals", "monolith" },
        { "POST", "/v1/campaigns/cmp_1/pledges", "monolith" },
        { "POST", "/v1/campaigns/cmp_1/settlement", "monolith" },
        { "GET", "/v1/webhooks/psp", "monolith" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Every_payments_route_reaches_payments_and_every_other_path_the_monolith(string method, string path, string service)
    {
        await using var monolith = await EchoService.Start("monolith");
        await using var payments = await EchoService.Start("payments");
        await using var gateway = Start(monolith, payments);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : new StringContent("{}") };
        request.Headers.Add("X-Forwarded-For", "203.0.113.20");

        var echo = await (await gateway.CreateClient().SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(service, echo.GetProperty("service").GetString());
        Assert.Equal(path, echo.GetProperty("path").GetString());
    }

    [Fact]
    public async Task The_webhook_reaches_payments_with_its_raw_body_and_signature_intact()
    {
        await using var monolith = await EchoService.Start("monolith");
        await using var payments = await EchoService.Start("payments");
        await using var gateway = Start(monolith, payments);
        // The signature covers these exact bytes: spacing, key order and non-ASCII text must not change.
        const string body = "{ \"id\":\"evt_1\",  \"type\" : \"setup_intent.succeeded\",\n\"data\":{\"note\":\"café ✓\"}}";
        using var request = Webhook("203.0.113.21", body);
        request.Headers.Remove("Psp-Signature");
        request.Headers.Add("Psp-Signature", "t=1767225600,v1=5f2b0c9a7e");

        var echo = await (await gateway.CreateClient().SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal("payments", echo.GetProperty("service").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)), echo.GetProperty("body").GetString());
        Assert.Equal("t=1767225600,v1=5f2b0c9a7e", echo.GetProperty("signature").GetString());
        Assert.StartsWith("application/json", echo.GetProperty("contentType").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hundred_webhooks_a_second_from_one_address_for_three_seconds_are_all_forwarded()
    {
        await using var monolith = await EchoService.Start("monolith");
        await using var payments = await EchoService.Start("payments");
        // The production limits: 20 a second per address with a burst of 40, shared through Valkey.
        await using var gateway = Start(monolith, payments, OwnBuckets(3));
        var client = gateway.CreateClient();
        const string psp = "198.51.100.30";

        var codes = new List<HttpStatusCode>();
        for (var tick = 0; tick < 30; tick++)
        {
            var started = DateTime.UtcNow;
            var sent = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
            {
                using var request = Webhook(psp);
                return (await client.SendAsync(request, Ct)).StatusCode;
            }));
            codes.AddRange(sent);
            var left = TimeSpan.FromMilliseconds(100) - (DateTime.UtcNow - started);
            if (left > TimeSpan.Zero)
            {
                await Task.Delay(left, Ct);
            }
        }

        Assert.Equal(300, codes.Count);
        Assert.All(codes, code => Assert.Equal(HttpStatusCode.OK, code));

        // The same address on any other route is still limited per address.
        var others = await Task.WhenAll(Enumerable.Range(0, 60).Select(async i =>
        {
            using var request = GatewayHost.From(psp, $"/v1/campaigns/cmp_{i}");
            return (await client.SendAsync(request, Ct)).StatusCode;
        }));
        Assert.Contains((HttpStatusCode)429, others);
    }

    [Fact]
    public async Task Above_the_route_wide_limit_webhooks_get_429_whatever_their_address()
    {
        await using var monolith = await EchoService.Start("monolith");
        await using var payments = await EchoService.Start("payments");
        await using var gateway = Start(monolith, payments, OwnBuckets(1), webhookPerSecond: 0.5, webhookBurst: 5);
        var client = gateway.CreateClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
        {
            using var request = Webhook($"198.51.100.{40 + i}");
            codes.Add((await client.SendAsync(request, Ct)).StatusCode);
        }

        // Ten callers, one request each: the per-address limit would let all ten through; the route's lets five.
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(5, codes.Count(c => c == (HttpStatusCode)429));
    }

    [Fact]
    public void The_webhook_route_allows_a_thousand_a_second_by_default()
    {
        using var gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.UseEnvironment("Testing"));

        var limits = gateway.Services.GetRequiredService<EdgeLimitOptions>();

        Assert.Equal(1000, limits.Webhook.PerSecond);
        Assert.Equal(1000, limits.Webhook.Burst);
        Assert.Equal(20, limits.PerSecond);
    }

    [Fact]
    public async Task A_flood_from_one_address_outside_the_psps_list_is_limited_per_address_and_the_psps_webhooks_still_pass()
    {
        await using var monolith = await EchoService.Start("monolith");
        await using var payments = await EchoService.Start("payments");
        // The production limits: 20 a second per address with a burst of 40; the route's bucket for the PSP: 50 at once.
        await using var gateway = Start(monolith, payments, OwnBuckets(2), webhookPerSecond: 0.5, webhookBurst: 50);
        var client = gateway.CreateClient();

        var flood = await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ =>
        {
            using var request = Webhook("203.0.113.9"); // not the PSP's: unsigned requests from anyone
            return (await client.SendAsync(request, Ct)).StatusCode;
        }));
        var psp = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
        {
            using var request = Webhook("198.51.100.7");
            psp.Add((await client.SendAsync(request, Ct)).StatusCode);
        }

        Assert.InRange(flood.Count(c => c == HttpStatusCode.OK), 1, 45); // its own burst of 40, and what a second refills
        Assert.Contains((HttpStatusCode)429, flood);
        Assert.All(psp, code => Assert.Equal(HttpStatusCode.OK, code)); // the route's bucket was not touched by the flood
    }
}
