using Microsoft.EntityFrameworkCore;

namespace Pledges.Infrastructure.Persistence;

/// <summary>
/// The connection string of Pledges' jobs role, which has the module's rights without the request
/// path's two-second transaction limit: the close, whose decision can move many pledges at once,
/// and the totals aggregator.
/// </summary>
internal sealed record PledgesJobsConnection(string Value)
{
    /// <summary>Points a scope's context at the jobs role. Call before the context is first used.</summary>
    public void Use(PledgesDbContext db) => db.Database.SetConnectionString(Value);
}
