using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// <c>GET /version</c> (lesson s5-l14): which service answers and which release it runs, the image tag
/// the service module sets as <c>SERVICE_VERSION</c> (the commit the image was built from). Outside
/// <c>/v1</c>, so the gateway never forwards it: an operator or a pipeline asks the task itself.
/// </summary>
public static class ServiceVersion
{
    public const string Setting = "SERVICE_VERSION";

    /// <summary>What a program reports when nothing set its version: a build on a developer's machine.</summary>
    public const string Unknown = "dev";

    public sealed record Answer(string Service, string Version);

    public static Answer Of(IConfiguration configuration, IHostEnvironment environment) =>
        new(environment.ApplicationName, configuration[Setting] is { Length: > 0 } v ? v : Unknown);

    public static IEndpointRouteBuilder MapVersion(this IEndpointRouteBuilder app)
    {
        app.MapGet("/version", (IConfiguration configuration, IHostEnvironment environment) => Results.Ok(Of(configuration, environment)))
            .AllowAnonymous()
            .ExcludeFromDescription();
        return app;
    }
}
