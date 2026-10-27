using BuildingBlocks.Application;

using Campaigns.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace Campaigns.Application;

public static class CampaignsApplication
{
    public static IServiceCollection AddCampaignsApplication(this IServiceCollection services) =>
        services
            .AddScoped<IValidator<CreateDraft>, CreateDraftValidator>()
            .AddScoped<ICommandHandler<CreateDraft, CampaignId>, CreateDraftHandler>()
            .AddScoped<ICommandHandler<PublishCampaign, CampaignStatus>, PublishCampaignHandler>()
            .AddScoped<ICommandHandler<AskToCancelFunding, CampaignStatus>, AskToCancelFundingHandler>()
            .AddScoped<ICommandHandler<CancelCampaign, CampaignStatus>, CancelCampaignHandler>()
            .AddCachedQueryHandler<GetCampaignCatalog, CampaignCatalog, GetCampaignCatalogHandler>()
            .AddScoped<IQueryHandler<GetCampaign, CampaignView>, GetCampaignHandler>();
}
