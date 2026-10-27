using System.Globalization;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Yarp.ReverseProxy.Model;

namespace CrowdFunding.Gateway;

public static class EdgeRateLimits
{
    public static IServiceCollection AddEdgeRateLimits(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<EdgeLimitOptions>().Bind(section)
            .Validate(o => o.PerSecond > 0 && o.Burst > 0 && o.GatewayTasks > 0, "EdgeLimits: PerSecond, Burst and GatewayTasks must be positive.")
            .Validate(o => o.Webhook.PerSecond > 0 && o.Webhook.Burst > 0, "EdgeLimits:Webhook: PerSecond and Burst must be positive.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<EdgeLimitOptions>>().Value);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<EdgeClients>();
        services.AddSingleton<LocalTokenBuckets>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<EdgeLimitOptions>();
            if (string.IsNullOrWhiteSpace(options.Valkey))
            {
                return new SharedBuckets(null);
            }

            // abortConnect=false: the gateway starts, and limits locally, even while Valkey is down.
            var config = ConfigurationOptions.Parse(options.Valkey);
            config.AbortOnConnectFail = false;
            config.ConnectTimeout = 1000;
            config.SyncTimeout = 250;
            config.AsyncTimeout = 250;
            return new SharedBuckets(new ValkeyTokenBuckets(ConnectionMultiplexer.Connect(config), options));
        });
        return services;
    }
}

/// <summary>The buckets every gateway task shares, or none when no Valkey is configured.</summary>
internal sealed record SharedBuckets(ValkeyTokenBuckets? Buckets);

/// <summary>
/// Refuses a client above its limit with 429 and Retry-After, before the request reaches any service.
/// It runs in YARP's proxy pipeline, so it applies to every request a route sends on, whatever the
/// case or spelling of its path, and to nothing else. Limits per user, which need a validated token,
/// are the services' job; the gateway passes the token through untouched. A route whose metadata says
/// <c>EdgeLimit: Webhook</c> has one bucket for all its callers instead of one per address.
/// </summary>
internal sealed partial class EdgeRateLimitMiddleware(
    RequestDelegate next,
    EdgeClients clients,
    SharedBuckets shared,
    LocalTokenBuckets local,
    ILogger<EdgeRateLimitMiddleware> logger)
{
    /// <summary>The client's address as the gateway decided it. Any value a caller sent is replaced.</summary>
    public const string ClientAddressHeader = "X-Client-Address";

    /// <summary>The route metadata key that picks a route-wide limit, and its one value.</summary>
    public const string LimitMetadata = "EdgeLimit";

    public const string WebhookLimit = "Webhook";

    private readonly ValkeyTokenBuckets? _shared = shared.Buckets;

    public async Task InvokeAsync(HttpContext context)
    {
        var client = clients.Of(context);
        context.Request.Headers[ClientAddressHeader] = client;

        var routeWide = context.GetReverseProxyFeature().Route.Config.Metadata?.GetValueOrDefault(LimitMetadata) == WebhookLimit;
        var decision = routeWide ? await TakeRouteWide(context.GetReverseProxyFeature().Route.Config.RouteId) : await Take(client);
        if (decision.Allowed)
        {
            await next(context);
            return;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new { reason = "rate_limited", retry_after_seconds = seconds });
    }

    private async Task<BucketDecision> Take(string client)
    {
        if (_shared is not { IsConnected: true })
        {
            return await local.Take(client);
        }

        try
        {
            return await _shared.Take(client);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogValkeyDown(logger, e.Message);
            return await local.Take(client);
        }
    }

    private async Task<BucketDecision> TakeRouteWide(string route)
    {
        if (_shared is not { IsConnected: true })
        {
            return await local.TakeRouteWide(route);
        }

        try
        {
            return await _shared.TakeRouteWide(route);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogValkeyDown(logger, e.Message);
            return await local.TakeRouteWide(route);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey unavailable, limiting in this process: {Reason}")]
    private static partial void LogValkeyDown(ILogger logger, string reason);
}
