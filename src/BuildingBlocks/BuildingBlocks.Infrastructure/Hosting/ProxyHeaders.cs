using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// Behind the load balancer a request comes from the ALB's address. The backer's address and the
/// original scheme arrive in X-Forwarded-For and X-Forwarded-Proto, which are trusted only from
/// inside the VPC (<c>ForwardedHeaders:KnownNetwork</c>, its CIDR); from anywhere else a client
/// could write them itself.
/// </summary>
public static class ProxyHeaders
{
    public static IServiceCollection AddProxyHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var network = configuration["ForwardedHeaders:KnownNetwork"];
        return services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            if (!string.IsNullOrEmpty(network))
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
    }
}
