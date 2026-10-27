using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gateway.Tests;

/// <summary>Who the client is, and that no spelling of a path gets past the limit.</summary>
public sealed class ClientTests
{
    private const string CloudFrontEdges = "130.176.0.0/16";

    [Fact]
    public async Task A_path_in_upper_case_is_limited_like_any_other()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var gateway = GatewayHost.Start(monolith.Address, perSecond: 1, burst: 1);
        var client = gateway.CreateClient();

        var codes = new List<HttpStatusCode>();
        foreach (var path in new[] { "/v1/campaigns/a", "/V1/campaigns/a", "/V1/CAMPAIGNS/a" })
        {
            using var request = GatewayHost.From("198.51.100.20", path);
            codes.Add((await client.SendAsync(request, ct)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, (HttpStatusCode)429, (HttpStatusCode)429], codes);
    }

    [Fact]
    public async Task The_gateway_replaces_any_client_address_a_caller_sends()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var gateway = GatewayHost.Start(monolith.Address);
        using var request = GatewayHost.From("198.51.100.21", "/v1/campaigns/a");
        request.Headers.Add("X-Client-Address", "10.0.0.1");

        var echo = await (await gateway.CreateClient().SendAsync(request, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("198.51.100.21", echo.GetProperty("clientAddress").GetString());
    }

    [Fact]
    public async Task Viewers_behind_one_CloudFront_edge_have_their_own_buckets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var gateway = GatewayHost.Start(monolith.Address, perSecond: 1, burst: 1, cloudFront: CloudFrontEdges);
        var client = gateway.CreateClient();

        HttpStatusCode Send(string viewer)
        {
            using var request = GatewayHost.From("130.176.1.1", "/v1/campaigns/a");
            request.Headers.Add("CloudFront-Viewer-Address", viewer);
            return client.SendAsync(request, ct).GetAwaiter().GetResult().StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, Send("203.0.113.1:50000"));
        Assert.Equal(HttpStatusCode.OK, Send("2001:db8::7:443"));
        Assert.Equal((HttpStatusCode)429, Send("203.0.113.1:50001"));
    }

    [Fact]
    public async Task A_viewer_header_from_outside_CloudFront_is_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monolith = await FakeMonolith.Start();
        await using var gateway = GatewayHost.Start(monolith.Address, perSecond: 1, burst: 1, cloudFront: CloudFrontEdges);
        var client = gateway.CreateClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++)
        {
            // A caller that is not CloudFront tries a new "viewer" each time to get a fresh bucket.
            using var request = GatewayHost.From("198.51.100.22", "/v1/campaigns/a");
            request.Headers.Add("CloudFront-Viewer-Address", $"203.0.113.{i + 10}:443");
            codes.Add((await client.SendAsync(request, ct)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, (HttpStatusCode)429], codes);
    }
}
