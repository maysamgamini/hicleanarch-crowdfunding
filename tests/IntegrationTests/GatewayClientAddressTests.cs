using System.Net;

using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace IntegrationTests;

/// <summary>The monolith takes the gateway's X-Client-Address only from a caller inside the VPC.</summary>
public sealed class GatewayClientAddressTests
{
    [Theory]
    [InlineData("10.0.3.7", "198.51.100.30", "198.51.100.30")]
    [InlineData("203.0.113.9", "198.51.100.30", "203.0.113.9")]
    public async Task The_client_address_is_trusted_only_from_inside_the_vpc(string caller, string header, string seen)
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["ForwardedHeaders:KnownNetwork"] = "10.0.0.0/16";
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(caller);
            return next(context);
        });
        app.UseGatewayClientAddress();
        app.Run(context => context.Response.WriteAsync(context.Connection.RemoteIpAddress!.ToString()));
        await app.StartAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(GatewayClientAddress.Header, header);
        var response = await app.GetTestClient().SendAsync(request, ct);

        Assert.Equal(seen, await response.Content.ReadAsStringAsync(ct));
    }
}
