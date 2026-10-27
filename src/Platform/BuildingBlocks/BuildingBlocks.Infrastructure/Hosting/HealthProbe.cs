namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// The container's health check (CQA-1518; every service's from s5-l01). A service behind Service Connect has
/// no load balancer to ask its readiness, and the aspnet image has no curl, so the program asks itself:
/// <c>dotnet CrowdFunding.&lt;Service&gt;.Host.dll health</c>
/// calls <c>/health/ready</c> on its own first HTTP port and exits 0 when ready, 1 otherwise. ECS runs it
/// as the container's healthCheck; the deployment circuit breaker and Service Connect read the result.
/// </summary>
public static class HealthProbe
{
    public const string Command = "health";

    /// <summary>The readiness endpoint on this container's first port (ASPNETCORE_HTTP_PORTS, 8080 by default).</summary>
    public static Uri Ready()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";
        return new Uri($"http://127.0.0.1:{ports.Split(';', ',')[0].Trim()}/health/ready");
    }

    /// <summary>0 when the endpoint answers 2xx within the time limit, 1 otherwise.</summary>
    public static async Task<int> Run(Uri ready, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout ?? TimeSpan.FromSeconds(4);
        try
        {
            using var response = await http.GetAsync(ready);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
