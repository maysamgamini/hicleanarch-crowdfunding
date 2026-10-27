using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Contracts;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Pledges.Application;
using Pledges.Contracts;
using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure;

/// <summary>
/// Everything a host needs to run the Pledges module. The monolith's host calls it next to the
/// other modules; a host of its own, if Pledges is ever split off, would call the same method.
/// </summary>
public static class PledgesModule
{
    /// <summary>The module connects as its own database role, which can reach only the pledges schema.</summary>
    public static IServiceCollection AddPledgesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Pledges")
            ?? throw new InvalidOperationException("ConnectionStrings:Pledges is not set.");
        services.AddDbContext<PledgesDbContext>((sp, db) => PledgesDbOptions
            .Configure((DbContextOptionsBuilder<PledgesDbContext>)db, connectionString)
            .AddInterceptors(sp.GetRequiredService<OutboxCommitInterceptor>()));
        services.AddModuleEvents<PledgesDbContext>(PledgesModuleName.Value);
        services.AddEventHandler<CampaignPublished, OpenFundingWindow>(PledgesModuleName.Value);
        services.AddSingleton<IModuleMigrator, PledgesMigrator>();
        services.AddSingleton(new ModuleDatabase(PledgesModuleName.Value, PledgesDbContext.Schema, connectionString));
        return services
            .AddPledgesApplication()
            .AddScoped<IFundingWindows, EfFundingWindows>();
    }
}
