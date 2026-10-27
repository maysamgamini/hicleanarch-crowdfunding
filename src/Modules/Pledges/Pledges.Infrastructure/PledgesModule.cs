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

using Payments.Contracts;

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
            .AddInterceptors(sp.GetRequiredService<OutboxCommitInterceptor<PledgesDbContext>>()));
        services.AddModuleEvents<PledgesDbContext>(PledgesModuleName.Value);
        services.AddEventHandler<CampaignPublished, OpenFundingWindow>(PledgesModuleName.Value);
        services.AddEventHandler<PaymentMethodSaved, ActivatePledge>(PledgesModuleName.Value);
        services.AddEventHandler<PledgeChargeSucceeded, FollowCharges>(PledgesModuleName.Value);
        services.AddEventHandler<PledgeChargeFailed, FollowCharges>(PledgesModuleName.Value);
        services.AddHostedService<HoldSweeper>();
        services.AddHostedService<CloseJob>();
        services.AddHostedService<PledgesMonitor>();
        services.AddHostedService<SnapshotCheck>();
        services.AddPublishedEvent<PledgeActivated>();
        services.AddPublishedEvent<PledgeChanged>();
        services.AddPublishedEvent<PledgeCancelled>();
        services.AddPublishedEvent<FundingCancelled>();
        services.AddPublishedEvent<CampaignFundingDecided>();
        services.AddPublishedEvent<PledgeChargeRequested>();
        services.AddKeyedScoped<ITransactionScope>(PledgesModuleName.Value, (sp, _) => new ModuleTransaction<PledgesDbContext>(sp.GetRequiredService<PledgesDbContext>()));
        services.AddSingleton<IModuleMigrator, PledgesMigrator>();
        services.AddSingleton(new ModuleDatabase(PledgesModuleName.Value, PledgesDbContext.Schema, connectionString));
        services.AddScoped<EfPledges>();
        return services
            .AddPledgesApplication()
            .AddScoped<IFundingWindows, EfFundingWindows>()
            .AddScoped<IPledges>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<IPledgeLedger>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<ITierSlots>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<IHolds>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<IClosing, EfClosing>()
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

                var result = await dispatcher.Send<PlacePledge, PledgePlaced>(
                    new PlacePledge(campaignId, user.UserId(), tierId, request.AmountMinor, request.PaymentMethodId, Cause(user, http)), ct);
                if (!result.IsSuccess)
                {
                    return Result.Failure<Reply>(result.Errors);
                }

                var placed = result.Value;
                object body = placed.ClientSecret is null
                    ? new { pledge_id = placed.PledgeId, status = placed.Status }
                    : new
                    {
                        pledge_id = placed.PledgeId,
                        status = placed.Status,
                        hold_expires_at = placed.HoldExpiresAt!.Value.UtcDateTime,
                        setup = new { provider = "psp", client_secret = placed.ClientSecret },
                    };
                return Result.Success(new Reply(StatusCodes.Status201Created, body, $"/v1/pledges/{placed.PledgeId}"));
            })).RequireAuthorization(Policies.Backer).WithTags("Pledges");

        // PATCH sends the pledge's new amount and tier; tier_id null means no reward.
        app.MapPatch("/v1/pledges/{id}", (string id, ChangeRequest request, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<PledgesDbContext> once) =>
            once.Run(http, user.UserId(), new { id, request }, async ct =>
            {
                if (!PublicId.TryParse(Pledge.IdPrefix, id, out var pledgeId))
                {
                    return Result.Failure<Reply>(PledgeErrors.PledgeNotFound);
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

                var result = await dispatcher.Send<ChangePledge, PledgeView>(
                    new ChangePledge(pledgeId, user.UserId(), request.AmountMinor, tierId, Cause(user, http)), ct);
                return result.IsSuccess
                    ? Result.Success(new Reply(StatusCodes.Status200OK, result.Value))
                    : Result.Failure<Reply>(result.Errors);
            })).RequireAuthorization(Policies.Backer).WithTags("Pledges");

        app.MapDelete("/v1/pledges/{id}", (string id, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<PledgesDbContext> once) =>
            once.Run(http, user.UserId(), new { id }, async ct =>
            {
                if (!PublicId.TryParse(Pledge.IdPrefix, id, out var pledgeId))
                {
                    return Result.Failure<Reply>(PledgeErrors.PledgeNotFound);
                }

                var result = await dispatcher.Send<CancelPledge, PledgeView>(new CancelPledge(pledgeId, user.UserId(), Cause(user, http)), ct);
                return result.IsSuccess
                    ? Result.Success(new Reply(StatusCodes.Status200OK, result.Value))
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

    /// <summary>What a ledger entry names as its cause: the caller and the request's idempotency key.</summary>
    private static string Cause(ClaimsPrincipal user, HttpContext http) =>
        $"request:{user.UserId():N}:{http.Request.Headers[IdempotentRequests<PledgesDbContext>.Header]}";
}

/// <summary>The JSON body of a pledge. <c>payment_method_id</c> is the PSP's reference to a saved card; without it, the card is saved first.</summary>
internal sealed record PledgeRequest(long AmountMinor, string? TierId, string? PaymentMethodId);

/// <summary>The JSON body of a change: the pledge's new amount and tier.</summary>
internal sealed record ChangeRequest(long AmountMinor, string? TierId);
