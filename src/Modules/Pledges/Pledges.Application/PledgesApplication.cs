using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace Pledges.Application;

public static class PledgesApplication
{
    public static IServiceCollection AddPledgesApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<CancelFunding, FundingCancelled>, CancelFundingHandler>();
}
