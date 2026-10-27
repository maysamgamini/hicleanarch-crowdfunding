using System.Security.Claims;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Pledges.Application;
using Pledges.Contracts;
using Pledges.Domain;
using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure;

/// <summary>
/// Everything a host needs to run the Pledges module: its services and its endpoints. The
/// monolith's host calls both; a host of its own, if Pledges is ever split off, would call the same two.
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
        services.AddPublishedEvent<PledgeActivated>();
        services.AddSingleton<IModuleMigrator, PledgesMigrator>();
        services.AddSingleton(new ModuleDatabase(PledgesModuleName.Value, PledgesDbContext.Schema, connectionString));
        services.AddScoped<EfPledges>();
        return services
            .AddPledgesApplication()
            .AddScoped<IFundingWindows, EfFundingWindows>()
            .AddScoped<IPledges>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<IPledgeLedger>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<ITierSlots>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<ICampaignTotals>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<IdempotentRequests<PledgesDbContext>>();
    }

    public static IEndpointRouteBuilder MapPledgesEndpoints(this IEndpointRouteBuilder app)
    {
        // Every change carries an Idempotency-Key and runs once per key (lesson s1-a04).
        app.MapPost("/v1/campaigns/{id}/pledges", (string id, PledgeRequest request, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<PledgesDbContext> once) =>
            once.Run(http, user.UserId(), new { id, request }, async ct =>
            {
                if (!PublicId.TryParse("cmp", id, out var campaignId))
                {
                    return Result.Failure<Reply>(PledgeErrors.CampaignNotFound);
                }

                Guid? tierId = null;
                if (request.TierId is not null)
                {
                    if (!PublicId.TryParse("tir", request.TierId, out var tier))
                    {
                        return Result.Failure<Reply>(PledgeErrors.UnknownTier);
                    }

                    tierId = tier;
                }

                var cause = $"request:{user.UserId():N}:{http.Request.Headers[IdempotentRequests<PledgesDbContext>.Header]}";
                var result = await dispatcher.Send<PlacePledge, PledgePlaced>(
                    new PlacePledge(campaignId, user.UserId(), tierId, request.AmountMinor, request.PaymentMethodId, cause), ct);
                return result.IsSuccess
                    ? Result.Success(new Reply(StatusCodes.Status201Created,
                        new { pledge_id = result.Value.PledgeId, status = result.Value.Status }, $"/v1/pledges/{result.Value.PledgeId}"))
                    : Result.Failure<Reply>(result.Errors);
            })).RequireAuthorization(Policies.Backer).WithTags("Pledges");

        app.MapGet("/v1/pledges/{id}", async (string id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!PublicId.TryParse(Pledge.IdPrefix, id, out var pledgeId))
            {
                return new[] { PledgeErrors.PledgeNotFound }.ToProblem();
            }

            var result = await dispatcher.Query<GetMyPledge, PledgeView>(new GetMyPledge(pledgeId, user.UserId()), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Errors.ToProblem();
        }).RequireAuthorization(Policies.Backer).WithTags("Pledges");

        return app;
    }
}

/// <summary>The JSON body of a pledge. <c>payment_method_id</c> is the PSP's reference to a saved card.</summary>
internal sealed record PledgeRequest(long AmountMinor, string? TierId, string? PaymentMethodId);
