using System.Security.Claims;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Application;
using Campaigns.Contracts;
using Campaigns.Domain;
using Campaigns.Infrastructure.Persistence;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace Campaigns.Infrastructure;

/// <summary>
/// Everything a host needs to run the Campaigns module: its services and its endpoints. The
/// monolith's host calls both; a host of its own, if the module is ever split off, would call the same two.
/// </summary>
public static class CampaignsModule
{
    /// <summary>The module connects as its own database role, which can reach only the campaigns schema.</summary>
    public static IServiceCollection AddCampaignsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Campaigns")
            ?? throw new InvalidOperationException("ConnectionStrings:Campaigns is not set.");
        services.AddDbContext<CampaignsDbContext>((sp, db) => CampaignsDbOptions
            .Configure((DbContextOptionsBuilder<CampaignsDbContext>)db, connectionString)
            .AddInterceptors(sp.GetRequiredService<OutboxCommitInterceptor<CampaignsDbContext>>()));
        services.AddModuleEvents<CampaignsDbContext>(CampaignsModuleName.Value);
        services.AddPublishedEvent<CampaignPublished>(CampaignsModuleName.Value);
        services.AddPublishedEvent<CampaignCancelled>(CampaignsModuleName.Value);
        services.AddPublishedEvent<CampaignCreatorRecorded>(CampaignsModuleName.Value);
        services.AddEventHandler<FundingCancelled, RecordFundingCancelled>(CampaignsModuleName.Value);
        services.AddEventHandler<CampaignFundingDecided, FollowFundingDecision>(CampaignsModuleName.Value);
        services.AddEventHandler<Payments.Contracts.CampaignSettled, FollowSettlement>(CampaignsModuleName.Value);
        // Queries read the reader endpoint; without one (development, tests) the writer, read-only.
        services.AddReadSide<CampaignsDbContext>(
            configuration.GetConnectionString("CampaignsRead") ?? connectionString, CampaignsDbOptions.Configure);
        services.AddEventHandler<CampaignPublished, CatalogCacheInvalidation>(CampaignsModuleName.Value);
        services.AddEventHandler<CampaignCancelled, CatalogCacheInvalidation>(CampaignsModuleName.Value);
        services.AddEventHandler<FundingCancelled, CatalogCacheInvalidation>(CampaignsModuleName.Value);
        services.AddEventHandler<CampaignFundingDecided, CatalogCacheInvalidation>(CampaignsModuleName.Value);
        services.AddEventHandler<Payments.Contracts.CampaignSettled, CatalogCacheInvalidation>(CampaignsModuleName.Value);
        services.AddSingleton<IModuleMigrator, CampaignsMigrator>();
        services.AddSingleton(new ModuleDatabase("Campaigns", CampaignsDbContext.Schema, connectionString));
        return services
            .AddCampaignsApplication()
            .AddScoped<ICampaignRepository, EfCampaignRepository>()
            .AddScoped<ICampaignReads, EfCampaignReads>()
            .AddScoped<IdempotentRequests<CampaignsDbContext>>()
            .AddSingleton<IdempotencyKeyPurge<CampaignsDbContext>>()
            .AddSingleton<IIdempotencyKeyPurge>(sp => sp.GetRequiredService<IdempotencyKeyPurge<CampaignsDbContext>>())
            .AddHostedService(sp => sp.GetRequiredService<IdempotencyKeyPurge<CampaignsDbContext>>());
    }

    public static IEndpointRouteBuilder MapCampaignsEndpoints(this IEndpointRouteBuilder app)
    {
        var campaigns = app.MapGroup("/v1/campaigns").WithTags("Campaigns");

        // Every change carries an Idempotency-Key and runs once per key (lesson s1-a04).
        campaigns.MapPost("/", (CreateCampaignRequest request, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<CampaignsDbContext> once) =>
            once.Run(http, user.UserId(), request, async ct =>
            {
                var command = new CreateDraft(
                    user.UserId(),
                    request.Title ?? "",
                    request.GoalMinor,
                    request.Currency ?? "",
                    request.Deadline,
                    [.. (request.Tiers ?? []).Select(t => new TierDraft(t.Title ?? "", t.MinPledgeMinor, t.Limit))]);
                var result = await dispatcher.Send<CreateDraft, CampaignId>(command, ct);
                return result.IsSuccess
                    ? Result.Success(new Reply(StatusCodes.Status201Created,
                        new { campaign_id = result.Value.ToString(), status = "DRAFT" }, $"/v1/campaigns/{result.Value}"))
                    : Result.Failure<Reply>(result.Errors);
            })).RequireAuthorization(Policies.Creator);

        campaigns.MapPost("/{id}/publish", (string id, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<CampaignsDbContext> once) =>
            once.Run(http, user.UserId(), new { id }, async ct =>
            {
                if (!CampaignId.TryParse(id, out var campaignId))
                {
                    return Result.Failure<Reply>(CampaignErrors.NotFound);
                }

                var result = await dispatcher.Send<PublishCampaign, CampaignStatus>(new PublishCampaign(campaignId, user.UserId()), ct);
                return result.IsSuccess
                    ? Result.Success(new Reply(StatusCodes.Status200OK, new { campaign_id = id, status = "LIVE" }))
                    : Result.Failure<Reply>(result.Errors);
            })).RequireAuthorization(Policies.Creator);

        // A draft's cancel. A live campaign's is Pledges' (s5-l05): since s5-l08 this path answers 409
        // funding_cancel_moved with a Link to it, and Campaigns sends Pledges no command.
        campaigns.MapPost("/{id}/cancel", (string id, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<CampaignsDbContext> once) =>
            once.Run(http, user.UserId(), new { id }, async ct =>
            {
                if (!CampaignId.TryParse(id, out var campaignId))
                {
                    return Result.Failure<Reply>(CampaignErrors.NotFound);
                }

                var result = await dispatcher.Send<CancelCampaign, CampaignStatus>(new CancelCampaign(campaignId, user.UserId()), ct);
                if (result.Errors.Contains(CampaignErrors.FundingCancelMoved))
                {
                    http.Response.Headers.Link = $"</v1/campaigns/{id}/funding/cancel>; rel=\"successor-version\"";
                }

                return result.IsSuccess
                    ? Result.Success(new Reply(StatusCodes.Status200OK, new { campaign_id = id, status = "CANCELLED" }))
                    : Result.Failure<Reply>(result.Errors);
            })).RequireAuthorization(Policies.Creator);

        // Anyone may read a published campaign; a draft only its creator, signed in. No totals (lesson s5-l08):
        // the page reads them from GET /v1/campaigns/{id}/totals, behind the CDN.
        campaigns.MapGet("/{id}", async (string id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!CampaignId.TryParse(id, out var campaignId))
            {
                return new[] { CampaignErrors.NotFound }.ToProblem();
            }

            var caller = user.Identity?.IsAuthenticated == true ? user.UserId() : (Guid?)null;
            var result = await dispatcher.Query<GetCampaign, CampaignView>(new GetCampaign(campaignId, caller), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Errors.ToProblem();
        }).AllowAnonymous().Produces<CampaignView>();

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
