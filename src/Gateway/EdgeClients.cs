using System.Net;

using Microsoft.AspNetCore.HttpOverrides;

namespace CrowdFunding.Gateway;

/// <summary>
/// Who the client is. After the forwarded-headers middleware, the connection's address is the
/// caller the public load balancer saw. When that caller is a CloudFront edge, every viewer behind
/// it would share one bucket, so the viewer's own address is taken from CloudFront-Viewer-Address,
/// which CloudFront writes itself. From any other caller that header is ignored: anyone can send it.
/// </summary>
internal sealed class EdgeClients(EdgeLimitOptions options)
{
    private const string ViewerAddressHeader = "CloudFront-Viewer-Address";

    private readonly IReadOnlyList<System.Net.IPNetwork> _cloudFront = [.. options.CloudFrontNetworks
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(System.Net.IPNetwork.Parse)];

    public string Of(HttpContext context)
    {
        var caller = context.Connection.RemoteIpAddress;
        if (caller is not null && FromCloudFront(caller)
            && TryViewer(context.Request.Headers[ViewerAddressHeader].ToString(), out var viewer))
        {
            return viewer.ToString();
        }

        return caller?.ToString() ?? "unknown";
    }

    private bool FromCloudFront(IPAddress caller)
    {
        var address = caller.IsIPv4MappedToIPv6 ? caller.MapToIPv4() : caller;
        return _cloudFront.Any(n => n.Contains(address));
    }

    /// <summary>The header is "address:port"; an IPv6 address has colons of its own, so split at the last one.</summary>
    private static bool TryViewer(string header, out IPAddress viewer)
    {
        viewer = IPAddress.None;
        var colon = header.LastIndexOf(':');
        return colon > 0 && IPAddress.TryParse(header.AsSpan(0, colon), out viewer!);
    }
}

/// <summary>How far the gateway trusts X-Forwarded-For: only from the load balancer, inside the VPC.</summary>
internal static class TrustedProxies
{
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
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
