using BuildingBlocks.Application;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using StackExchange.Redis;

namespace BuildingBlocks.Infrastructure.Caching;

public static class QueryCacheRegistration
{
    /// <summary>
    /// Valkey when <c>ConnectionStrings:Valkey</c> is set, no cache otherwise. One connection per
    /// program; it reconnects on its own.
    /// </summary>
    public static IServiceCollection AddQueryCache(this IServiceCollection services, IConfiguration configuration)
    {
        var valkey = configuration.GetConnectionString("Valkey");
        if (string.IsNullOrWhiteSpace(valkey))
        {
            return services.AddSingleton<IQueryCache, NoQueryCache>();
        }

        var options = ConfigurationOptions.Parse(valkey);
        options.AbortOnConnectFail = false;
        services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        return services.AddSingleton<IQueryCache, ValkeyQueryCache>();
    }
}
