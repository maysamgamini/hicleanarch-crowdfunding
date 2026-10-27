using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Every endpoint a started host has mapped, by method and route, as <c>GET /v1/campaigns/{id}</c>. With
/// <c>payments</c>, the system's: the monolith's and its Payments service's (s4-l10), each route once.
/// </summary>
internal static class MappedEndpoints
{
    public static IReadOnlyList<(string Name, RouteEndpoint Endpoint)> Of(WebApplicationFactory<Program> host, bool payments = false)
    {
        _ = host.Server; // the endpoints are mapped when the host starts
        IEnumerable<IServiceProvider> hosts = payments ? [host.Services, host.Payments().Services] : [host.Services];
        return
        [
            .. hosts.SelectMany(h => h.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
                .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                    .Select(method => ($"{method} {e.RoutePattern.RawText}", e)))
                .DistinctBy(e => e.Item1)
                .OrderBy(e => e.Item1, StringComparer.Ordinal),
        ];
    }
}
