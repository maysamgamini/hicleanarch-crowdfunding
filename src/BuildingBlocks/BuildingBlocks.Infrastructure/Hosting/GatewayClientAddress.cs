using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// Behind the gateway, a request reaches the monolith through the internal load balancer, so
/// X-Forwarded-For's last entry (the one ForwardLimit = 1 trusts) is the gateway task, not the
/// client. The gateway decides who the client is, once, and sends it in X-Client-Address, which it
/// always overwrites. The monolith takes that value only from a caller inside the VPC
/// (ForwardedHeaders:KnownNetwork); from anywhere else, or with no network configured, it is ignored.
/// ForwardLimit stays 1: trusting a second X-Forwarded-For entry would trust whatever the client wrote there.
/// </summary>
public static class GatewayClientAddress
{
    public const string Header = "X-Client-Address";

    public static IApplicationBuilder UseGatewayClientAddress(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var known = configuration["ForwardedHeaders:KnownNetwork"];
        var network = string.IsNullOrEmpty(known) ? (System.Net.IPNetwork?)null : System.Net.IPNetwork.Parse(known);

        return app.Use(async (context, next) =>
        {
            var caller = context.Connection.RemoteIpAddress;
            if (network is { } inside && caller is not null
                && inside.Contains(caller.IsIPv4MappedToIPv6 ? caller.MapToIPv4() : caller)
                && IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var client))
            {
                context.Connection.RemoteIpAddress = client;
            }

            await next(context);
        });
    }
}
