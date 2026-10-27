using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

internal static class GatewayHost
{
    /// <summary>A request as the load balancer sends it: with the client's address in X-Forwarded-For.</summary>
    public static HttpRequestMessage From(string clientIp, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-For", clientIp);
        return request;
    }

    /// <summary>A gateway in memory, routing to <paramref name="monolith"/>, with the given limits.</summary>
    public static WebApplicationFactory<Program> Start(
        string monolith, string valkey = "", int perSecond = 1, int burst = 3, int tasks = 1, string cloudFront = "") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web => web
            .UseEnvironment("Testing")
            .UseSetting("ReverseProxy:Clusters:monolith:Destinations:monolith:Address", monolith)
            // Campaigns' routes (s5-l08) reach the monolith until the Campaigns service serves them, as in AWS
            // (campaigns_serves_routes); CampaignsRouteTests points them elsewhere.
            .UseSetting("ReverseProxy:Clusters:campaigns:Destinations:campaigns:Address", monolith)
            .UseSetting("EdgeLimits:Valkey", valkey)
            .UseSetting("EdgeLimits:PerSecond", perSecond.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("EdgeLimits:Burst", burst.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("EdgeLimits:GatewayTasks", tasks.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("EdgeLimits:CloudFrontNetworks", cloudFront));
}
