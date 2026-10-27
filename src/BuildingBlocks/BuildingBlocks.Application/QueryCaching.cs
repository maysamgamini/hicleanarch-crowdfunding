using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Application;

/// <summary>
/// A query whose answer may be served from the cache. The key starts with the module's name, so
/// two modules never share an entry; the time to live is how stale the answer may be at most.
/// </summary>
public interface ICacheableQuery
{
    string CacheKey { get; }

    TimeSpan TimeToLive { get; }
}

/// <summary>
/// Cache-aside: read the cache, and on a miss load the answer and store it. Only successful
/// answers are stored. A cache that cannot be reached is a miss, never an error.
/// </summary>
public interface IQueryCache
{
    Task<Result<T>> GetOrLoad<T>(string key, TimeSpan timeToLive, Func<CancellationToken, Task<Result<T>>> load, CancellationToken cancellationToken);

    Task Invalidate(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps a query handler with the same interface and adds the cache around it. The handler, the
/// domain and the commands do not know it exists: removing it changes speed, never behaviour.
/// </summary>
public sealed class CachingQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> inner, IQueryCache cache)
    : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>, ICacheableQuery
{
    public Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken) =>
        cache.GetOrLoad(query.CacheKey, query.TimeToLive, ct => inner.Handle(query, ct), cancellationToken);
}

public static class QueryCaching
{
    /// <summary>Registers <typeparamref name="THandler"/> behind the caching decorator: opt-in, one query at a time.</summary>
    public static IServiceCollection AddCachedQueryHandler<TQuery, TResult, THandler>(this IServiceCollection services)
        where TQuery : IQuery<TResult>, ICacheableQuery
        where THandler : class, IQueryHandler<TQuery, TResult>
    {
        services.AddScoped<THandler>();
        return services.AddScoped<IQueryHandler<TQuery, TResult>>(sp =>
            new CachingQueryHandler<TQuery, TResult>(sp.GetRequiredService<THandler>(), sp.GetRequiredService<IQueryCache>()));
    }
}
