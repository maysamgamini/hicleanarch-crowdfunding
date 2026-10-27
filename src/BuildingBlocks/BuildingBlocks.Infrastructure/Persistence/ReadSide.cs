using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// A module's context on its read side: the Aurora reader endpoint, which may be a moment behind the
/// writer. It tracks nothing and its sessions refuse writes, also where the read side is the writer
/// itself (development and tests).
/// </summary>
public sealed class ReadOnlyDb<TContext>(TContext context) : IAsyncDisposable
    where TContext : DbContext
{
    public TContext Context { get; } = context;

    public ValueTask DisposeAsync() => Context.DisposeAsync();
}

public static class ReadSideRegistration
{
    /// <summary>
    /// Registers <see cref="ReadOnlyDb{TContext}"/> for one module. The module passes the same
    /// configuration it uses for its writer, so both sides see one model.
    /// </summary>
    public static IServiceCollection AddReadSide<TContext>(
        this IServiceCollection services,
        string connectionString,
        Func<DbContextOptionsBuilder<TContext>, string, DbContextOptionsBuilder<TContext>> configure)
        where TContext : DbContext
    {
        var readOnly = ReadOnly(connectionString);
        return services.AddScoped(_ =>
        {
            var options = configure(new DbContextOptionsBuilder<TContext>(), readOnly)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            return new ReadOnlyDb<TContext>((TContext)Activator.CreateInstance(typeof(TContext), options)!);
        });
    }

    /// <summary>The same connection, with every transaction read-only from its first statement.</summary>
    public static string ReadOnly(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Options = $"{builder.Options} -c default_transaction_read_only=on".Trim();
        return builder.ConnectionString;
    }
}
