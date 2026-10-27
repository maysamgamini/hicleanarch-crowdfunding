using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Pledges.Contracts;

namespace Pledges.Infrastructure.Persistence;

/// <summary>Brings the Pledges schema up to date. Run by the host's <c>migrate</c> command, never at start-up.</summary>
internal sealed class PledgesMigrator : IModuleMigrator
{
    public string Module => PledgesModuleName.Value;

    public string Schema => PledgesDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        var options = PledgesDbOptions.Configure(new DbContextOptionsBuilder<PledgesDbContext>(), ownerConnectionString).Options;
        await using var db = new PledgesDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
