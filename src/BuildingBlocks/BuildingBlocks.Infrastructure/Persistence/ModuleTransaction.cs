using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// One transaction of one module's database context, for work that does not come through an
/// idempotent request: a command from another module, a webhook, a job's pass. A refusal rolls it
/// back; a success commits it, and the outbox's events are handed out after the commit. Inside a
/// transaction the context already has, it joins that one.
/// </summary>
public sealed class ModuleTransaction<TContext>(TContext db) : ITransactionScope
    where TContext : DbContext
{
    public async Task<Result<T>> Run<T>(Func<CancellationToken, Task<Result<T>>> work, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await work(cancellationToken);
        if (result.IsSuccess)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return result;
    }
}
