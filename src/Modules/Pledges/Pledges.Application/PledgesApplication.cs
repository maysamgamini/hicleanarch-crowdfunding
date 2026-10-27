using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace Pledges.Application;

public static class PledgesApplication
{
    public static IServiceCollection AddPledgesApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<CancelFunding, CancelFundingAccepted>, CancelFundingHandler>()
            .AddScoped<ICommandHandler<PlacePledge, PledgePlaced>, PlacePledgeHandler>()
            .AddScoped<ICommandHandler<ResumeCardSetup, PledgePlaced>, ResumeCardSetupHandler>()
            .AddScoped<ICommandHandler<ExpireHolds, int>, ExpireHoldsHandler>()
            .AddScoped<ICommandHandler<CloseDueCampaigns, int>, CloseDueCampaignsHandler>()
            .AddScoped<ICommandHandler<ChangePledge, PledgeView>, ChangePledgeHandler>()
            .AddScoped<ICommandHandler<CancelPledge, PledgeView>, CancelPledgeHandler>()
            .AddScoped<IQueryHandler<GetMyPledge, PledgeView>, GetMyPledgeHandler>()
            .AddScoped<IQueryHandler<GetPublicTotals, PublicTotalsReply>, GetPublicTotalsHandler>();
}
