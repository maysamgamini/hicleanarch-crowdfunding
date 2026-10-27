using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>Every endpoint a started host has mapped, by method and route, as <c>GET /v1/campaigns/{id}</c>.</summary>
internal static class MappedEndpoints
{
    public static IReadOnlyList<(string Name, RouteEndpoint Endpoint)> Of(WebApplicationFactory<Program> host)
    {
        _ = host.Server; // the endpoints are mapped when the host starts
        return
        [
            .. host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                    .Select(method => ($"{method} {e.RoutePattern.RawText}", e)))
                .OrderBy(e => e.Item1, StringComparer.Ordinal),
        ];
    }
}
