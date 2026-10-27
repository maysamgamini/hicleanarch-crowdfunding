using System.Globalization;

using BuildingBlocks.Application;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using StackExchange.Redis;

namespace BuildingBlocks.Infrastructure.Valkey;

public static class ValkeyRegistration
{
    /// <summary>
    /// How every Valkey connection of this program is made, from <c>ConnectionStrings:Valkey</c>
    /// (which may carry <c>ssl=true,password=...</c>). The timeouts are this method's, not the
    /// string's: Valkey answers in well under a millisecond in its own zone, so a call that takes half a
    /// second is failing, and a cache miss or the degraded buckets are better than the client's
    /// default 5 s wait on every call. Keep-alive pings every 10 s find a dead connection after a
    /// failover sooner than the default 60 s. A connection that fails at first keeps retrying.
    /// </summary>
    public static ConfigurationOptions ClientOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 1_000;
        options.SyncTimeout = 500;
        options.AsyncTimeout = 500;
        options.KeepAlive = 10;
        return options;
    }

    /// <summary>
    /// The shared Valkey connection and the token buckets on it, when <c>ConnectionStrings:Valkey</c>
    /// is set; buckets in memory otherwise. Every module that needs them calls this. The connection
    /// replaces any registered before it, so the program has one, made by <see cref="ClientOptions"/>.
    /// <c>Valkey:Tasks</c> is the most tasks of this program that run at once; while Valkey cannot be
    /// reached, each task keeps that share of every limit in its own memory.
    /// </summary>
    public static IServiceCollection AddSharedValkey(this IServiceCollection services, IConfiguration configuration)
    {
        var valkey = configuration.GetConnectionString("Valkey");
        if (string.IsNullOrWhiteSpace(valkey))
        {
            services.TryAddSingleton<ITokenBuckets, InProcessTokenBuckets>();
            return services;
        }

        var tasks = configuration.GetValue("Valkey:Tasks", DegradedTokenBuckets.DefaultTasks);
        if (tasks < 1)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Valkey:Tasks must be at least 1, not {tasks}."));
        }

        services.Replace(ServiceDescriptor.Singleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(ClientOptions(valkey))));
        services.Replace(ServiceDescriptor.Singleton(sp => new DegradedTokenBuckets(tasks, sp.GetRequiredService<TimeProvider>())));
        services.TryAddSingleton<ITokenBuckets, ValkeyTokenBuckets>();
        return services;
    }

    /// <summary>Whether a Valkey connection is configured: a module picks its Valkey adapters or its in-memory ones.</summary>
    public static bool HasValkey(this IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Valkey"));
}
