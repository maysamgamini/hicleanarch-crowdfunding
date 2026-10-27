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

        // The program's one connection, with the short timeouts every Valkey client here uses.
        services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(Valkey.ValkeyRegistration.ClientOptions(valkey)));
        return services.AddSingleton<IQueryCache, ValkeyQueryCache>();
    }
}
