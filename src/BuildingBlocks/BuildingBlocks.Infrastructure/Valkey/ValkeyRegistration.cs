using BuildingBlocks.Application;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using StackExchange.Redis;

namespace BuildingBlocks.Infrastructure.Valkey;

public static class ValkeyRegistration
{
    /// <summary>
    /// The shared Valkey connection and the token buckets on it, when <c>ConnectionStrings:Valkey</c>
    /// is set; buckets in memory otherwise. Every module that needs them calls this; the first call registers.
    /// </summary>
    public static IServiceCollection AddSharedValkey(this IServiceCollection services, IConfiguration configuration)
    {
        var valkey = configuration.GetConnectionString("Valkey");
        if (string.IsNullOrWhiteSpace(valkey))
        {
            services.TryAddSingleton<ITokenBuckets, InProcessTokenBuckets>();
            return services;
        }

        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(valkey);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        services.TryAddSingleton<ITokenBuckets, ValkeyTokenBuckets>();
        return services;
    }

    /// <summary>Whether a Valkey connection is configured: a module picks its Valkey adapters or its in-memory ones.</summary>
    public static bool HasValkey(this IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Valkey"));
}
