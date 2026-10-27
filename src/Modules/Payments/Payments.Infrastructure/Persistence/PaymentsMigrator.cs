using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Payments.Contracts;

namespace Payments.Infrastructure.Persistence;

/// <summary>Brings the Payments schema up to date. Run by the host's <c>migrate</c> command, never at start-up.</summary>
internal sealed class PaymentsMigrator : IModuleMigrator
{
    public string Module => PaymentsModuleName.Value;

    public string Schema => PaymentsDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        var options = PaymentsDbOptions.Configure(new DbContextOptionsBuilder<PaymentsDbContext>(), ownerConnectionString).Options;
        await using var db = new PaymentsDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
