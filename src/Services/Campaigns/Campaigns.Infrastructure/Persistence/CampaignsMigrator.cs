using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Campaigns.Infrastructure.Persistence;

/// <summary>Brings the Campaigns schema up to date. Run by the host's <c>migrate</c> command, never at start-up.</summary>
internal sealed class CampaignsMigrator : IModuleMigrator
{
    public string Module => "Campaigns";

    public string Schema => CampaignsDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        var options = CampaignsDbOptions.Configure(new DbContextOptionsBuilder<CampaignsDbContext>(), ownerConnectionString).Options;
        await using var db = new CampaignsDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
