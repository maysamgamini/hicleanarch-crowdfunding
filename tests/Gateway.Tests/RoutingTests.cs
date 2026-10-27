using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gateway.Tests;

public sealed class RoutingTests
{
    [Fact]
    public async Task An_api_request_reaches_the_monolith_with_its_token_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var gateway = GatewayHost.Start(monolith.Address, burst: 100);
        var client = gateway.CreateClient();
        using var request = GatewayHost.From("203.0.113.7", "/v1/campaigns/cmp_1?x=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "a.b.c");

        var response = await client.SendAsync(request, ct);
        var echo = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/v1/campaigns/cmp_1?x=1", echo.GetProperty("path").GetString());
        Assert.Equal("Bearer a.b.c", echo.GetProperty("authorization").GetString());
        Assert.Equal("203.0.113.7", echo.GetProperty("forwardedFor").GetString());
    }

    [Fact]
    public async Task A_path_outside_the_api_is_not_proxied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var gateway = GatewayHost.Start(monolith.Address);
        var client = gateway.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", ct)).StatusCode);
    }
}
