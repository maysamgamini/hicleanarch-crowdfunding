using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;

using Campaigns.Application;
using Campaigns.Domain;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Campaigns.Infrastructure;

/// <summary>
/// Everything a host needs to run the Campaigns module: its services and its endpoints. The
/// monolith's host calls both; a host of its own, if the module is ever split off, would call the same two.
/// </summary>
public static class CampaignsModule
{
    public static IServiceCollection AddCampaignsModule(this IServiceCollection services) =>
        services
            .AddCampaignsApplication()
            .AddSingleton<ICampaignRepository, InMemoryCampaignRepository>();

    public static IEndpointRouteBuilder MapCampaignsEndpoints(this IEndpointRouteBuilder app)
    {
        var campaigns = app.MapGroup("/v1/campaigns").WithTags("Campaigns");

        campaigns.MapPost("/", async (CreateCampaignRequest request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var command = new CreateDraft(
                request.Title ?? "",
                request.GoalMinor,
                request.Currency ?? "",
                request.Deadline,
                [.. (request.Tiers ?? []).Select(t => new TierDraft(t.Title ?? "", t.MinPledgeMinor, t.Limit))]);
            var result = await dispatcher.Send<CreateDraft, CampaignId>(command, ct);
            return result.IsSuccess
                ? Results.Created($"/v1/campaigns/{result.Value}", new { campaign_id = result.Value.ToString(), status = "DRAFT" })
                : result.Errors.ToProblem();
        });

        campaigns.MapGet("/{id}", async (string id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!CampaignId.TryParse(id, out var campaignId))
            {
                return new[] { CampaignErrors.NotFound }.ToProblem();
            }

            var result = await dispatcher.Query<GetCampaign, CampaignView>(new GetCampaign(campaignId), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Errors.ToProblem();
        });

        return app;
    }
}

/// <summary>The JSON body of <c>POST /v1/campaigns</c>. Missing fields arrive as null and are refused by the rules.</summary>
internal sealed record CreateCampaignRequest(
    string? Title,
    long GoalMinor,
    string? Currency,
    DateTimeOffset Deadline,
    IReadOnlyList<TierRequest>? Tiers);

internal sealed record TierRequest(string? Title, long MinPledgeMinor, int? Limit);
