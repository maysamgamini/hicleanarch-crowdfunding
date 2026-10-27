using System.Security.Claims;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Valkey;

using Campaigns.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Payments.Contracts;

using Pledges.Application;
using Pledges.Contracts;
using Pledges.Domain;
using Pledges.Infrastructure.HotTier;
using Pledges.Infrastructure.Persistence;
using Pledges.Infrastructure.Sharding;
using Pledges.Infrastructure.Totals;

using StackExchange.Redis;

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
        var jobsConnection = configuration.GetConnectionString("PledgesJobs")
            ?? throw new InvalidOperationException("ConnectionStrings:PledgesJobs is not set.");
        var fence = TimeFence.Default with
        {
            WaitAfterEnd = configuration.GetValue("Pledges:Close:WaitAfterEnd", TimeFence.Default.WaitAfterEnd),
            TierWait = configuration.GetValue("Pledges:TierWait", TimeFence.Default.TierWait),
        };
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
        services.AddHostedService<TotalsCheck>();
        AddDisplayTotals(services, configuration);
        services.AddPublishedEvent<PledgeActivated>(PledgesModuleName.Value);
        services.AddPublishedEvent<PledgeChanged>(PledgesModuleName.Value);
        services.AddPublishedEvent<PledgeCancelled>(PledgesModuleName.Value);
        services.AddPublishedEvent<FundingCancelled>(PledgesModuleName.Value);
        services.AddPublishedEvent<PledgeReleased>(PledgesModuleName.Value);
        services.AddPublishedEvent<CampaignFundingDecided>(PledgesModuleName.Value);
        services.AddPublishedEvent<PledgeChargeRequested>(PledgesModuleName.Value);
        services.AddKeyedScoped<ITransactionScope>(PledgesModuleName.Value, (sp, _) => new ModuleTransaction<PledgesDbContext>(sp.GetRequiredService<PledgesDbContext>()));
        services.AddSingleton<IModuleMigrator, PledgesMigrator>();
        // The request path's role: every transaction that can add a ledger entry after reading LIVE is
        // ended by the database after two seconds (lesson s3-l05). The jobs role has the same rights, no limit.
        services.AddSingleton(new ModuleDatabase(PledgesModuleName.Value, PledgesDbContext.Schema, connectionString)
        {
            RoleSettings = [$"transaction_timeout = '{(int)fence.TransactionLimit.TotalMilliseconds}ms'"],
        });
        services.AddSingleton(new ModuleDatabase(PledgesModuleName.Value + "Jobs", PledgesDbContext.Schema, jobsConnection));
        services.AddSingleton(new PledgesJobsConnection(jobsConnection));
        services.AddSingleton(fence);
        services.AddExceptionHandler<DatabaseBusyHandler>();
        services.AddReadSide<PledgesDbContext>(
            configuration.GetConnectionString("PledgesRead") ?? connectionString, PledgesDbOptions.Configure);
        services.AddScoped<EfPledges>();
        services.AddOptions<MarkingOptions>().Bind(configuration.GetSection("Pledges"))
            .Validate(m => m.MarkBatchSize > 0 && m.MarkBatchesPerPass > 0 && m.CancelReleaseBatchSize > 0,
                "Pledges: MarkBatchSize, MarkBatchesPerPass and CancelReleaseBatchSize must be positive.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MarkingOptions>>().Value);
        services.AddSharedValkey(configuration);
        if (configuration.HasValkey())
        {
            services.AddSingleton<ISoldOutTiers, ValkeySoldOutTiers>();
        }
        else
        {
            services.AddSingleton<ISoldOutTiers, NoSoldOutTiers>();
        }

        // Sums are job work: the shard's connection is the jobs role, without the request path's two-second limit.
        services.AddSingleton<ICampaignShards>(new SingleShard(jobsConnection));
        services.AddSingleton<ILedgerSums, ShardedLedgerSums>();
        services.AddHostedService<LedgerPartitionJob>();
        return services
            .AddPledgesApplication()
            .AddScoped<EfFundingWindows>()
            .AddScoped<IFundingWindows, TimeFencedFundingWindows>()
            .AddScoped<IPledges>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<IPledgeLedger>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<ITierSlots>(sp => new SoldOutAwareTierSlots(sp.GetRequiredService<EfPledges>(), sp.GetRequiredService<ISoldOutTiers>()))
            .AddScoped<IHolds>(sp => sp.GetRequiredService<EfPledges>())
            .AddScoped<IClosing, EfClosing>()
            .AddScoped<ILastDayRule, EfLastDayRule>()
            .AddScoped<ICampaignTotals, CachedCampaignTotals>()
            .AddScoped<IPublicTotalsReads, EfPublicTotals>()
            .AddScoped<IdempotentRequests<PledgesDbContext>>()
            // The hourly purge of week-old keys, as the jobs role: pledges_app's two-second limit is for requests.
            .AddSingleton(sp => new PurgeConnection<PledgesDbContext>(sp.GetRequiredService<PledgesJobsConnection>().Use))
            .AddSingleton<IdempotencyKeyPurge<PledgesDbContext>>()
            .AddSingleton<IIdempotencyKeyPurge>(sp => sp.GetRequiredService<IdempotencyKeyPurge<PledgesDbContext>>())
            .AddHostedService(sp => sp.GetRequiredService<IdempotencyKeyPurge<PledgesDbContext>>());
    }

    /// <summary>
    /// The aggregator and where it keeps display totals: Valkey when ConnectionStrings:Valkey is set
    /// (every task shares it), otherwise this process's memory (one task: development and tests).
    /// </summary>
    private static void AddDisplayTotals(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<DirtyCampaigns>();
        services.AddEventHandler<PledgeActivated, MarkCampaignDirty>(PledgesModuleName.Value);
        services.AddEventHandler<PledgeChanged, MarkCampaignDirty>(PledgesModuleName.Value);
        services.AddEventHandler<PledgeCancelled, MarkCampaignDirty>(PledgesModuleName.Value);
        services.AddHostedService<TotalsAggregator>();
        services.AddScoped<ITotalsSource, EfTotalsSource>();
        if (configuration.GetConnectionString("Valkey") is { } valkey)
        {
            services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(valkey)); // one connection, shared with the query cache
            services.AddSingleton<ITotalsStore, ValkeyTotalsStore>();
        }
        else
        {
            services.AddSingleton<ITotalsStore, InMemoryTotalsStore>();
        }
    }

    public static IEndpointRouteBuilder MapPledgesEndpoints(this IEndpointRouteBuilder app)
    {
        // Every change carries an Idempotency-Key and runs once per key (lesson s1-a04).
        // A new card's setup is started at the PSP before the pledge's transaction opens, so no PSP
        // call ever runs while that transaction holds the funding window or a tier row. A retry of a
        // request that has finished gets its stored reply first and never reaches Payments.
        app.MapPost("/v1/campaigns/{id}/pledges", async (string id, PledgeRequest request, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<PledgesDbContext> once) =>
        {
            NewCard? newCard = null;
            if (request.PaymentMethodId is null)
            {
                if (await once.TryReplay(http, user.UserId(), new { id, request }) is { } replayed)
                {
                    return replayed;
                }

                var pledgeId = Guid.CreateVersion7();
                var setup = await dispatcher.Send<StartCardSetup, CardSetupStarted>(new StartCardSetup(pledgeId, user.UserId()), http.RequestAborted);
                if (setup.IsSuccess)
                {
                    newCard = new NewCard(pledgeId, setup.Value.ClientSecret);
                }
                else if (setup.Errors[0].RetryAfter is { } wait)
                {
                    // The card-setup lane is full (lesson s3-a04): the pledge and its place are kept, and
                    // the backer asks for the card form later.
                    newCard = new NewCard(pledgeId, null, wait);
                }
                else
                {
                    return new[] { PledgeErrors.CardSetupFailed }.ToProblem();
                }
            }

            return await once.Run(http, user.UserId(), new { id, request }, async ct =>
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
                    new PlacePledge(campaignId, user.UserId(), tierId, request.AmountMinor, request.PaymentMethodId, Cause(user, http), newCard), ct);
                if (!result.IsSuccess)
                {
                    return Result.Failure<Reply>(result.Errors);
                }

                var placed = result.Value;
                return Result.Success(new Reply(StatusCodes.Status201Created, Body(placed), $"/v1/pledges/{placed.PledgeId}"));
            });
        }).RequireAuthorization(Policies.Backer).AddEndpointFilter<SoldOutShortCircuit>().WithTags("Pledges");

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

        // Public, polled by every campaign page, and cached by CloudFront for at most 2 seconds.
        app.MapGet("/v1/campaigns/{id}/totals", async (string id, HttpContext http, IDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!PublicId.TryParse("cmp", id, out var campaignId))
            {
                return new[] { PledgeErrors.CampaignNotFound }.ToProblem();
            }

            var result = await dispatcher.Query<GetPublicTotals, PublicTotalsReply>(new GetPublicTotals(campaignId), ct);
            if (!result.IsSuccess)
            {
                http.Response.Headers.CacheControl = "no-store";
                return result.Errors.ToProblem();
            }

            http.Response.Headers.CacheControl = result.Value.MaxAgeSeconds > 0
                ? $"public, max-age={result.Value.MaxAgeSeconds}"
                : "no-store";
            return Results.Ok(result.Value.View);
        }).AllowAnonymous().WithTags("Pledges");

        // The card form, asked for again by a pledge that waited in the card-setup lane (lesson s3-a04).
        // Like the pledge endpoint, it checks the pledge and starts the setup before the key's
        // transaction opens, so no PSP call runs while a Pledges transaction is open; the setup is
        // idempotent per pledge, so a retry gets the same form. The key's transaction only stores the reply.
        app.MapPost("/v1/pledges/{id}/card-setup", async (string id, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<PledgesDbContext> once) =>
        {
            if (await once.TryReplay(http, user.UserId(), new { id }) is { } replayed)
            {
                return replayed;
            }

            if (!PublicId.TryParse(Pledge.IdPrefix, id, out var pledgeId))
            {
                return new[] { PledgeErrors.PledgeNotFound }.ToProblem();
            }

            var result = await dispatcher.Send<ResumeCardSetup, PledgePlaced>(new ResumeCardSetup(pledgeId, user.UserId()), http.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.Errors.ToProblem();
            }

            return await once.Run(http, user.UserId(), new { id },
                _ => Task.FromResult(Result.Success(new Reply(StatusCodes.Status200OK, Body(result.Value)))));
        }).RequireAuthorization(Policies.Backer).WithTags("Pledges");

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

    /// <summary>
    /// A placed pledge as the API shows it. A pledge waiting for its card carries its hold and the
    /// card form's secret, or, while the card-setup lane is full, when to ask for it again.
    /// </summary>
    private static object Body(PledgePlaced placed) =>
        placed.Status != "PENDING_SETUP"
            ? new { pledge_id = placed.PledgeId, status = placed.Status }
            : new
            {
                pledge_id = placed.PledgeId,
                status = placed.Status,
                hold_expires_at = placed.HoldExpiresAt!.Value.UtcDateTime,
                setup = placed.ClientSecret is not null
                    ? (object)new { provider = "psp", client_secret = placed.ClientSecret }
                    : new { provider = "psp", client_secret = (string?)null, retry_after_seconds = (int)Math.Ceiling(placed.SetupRetryAfter!.Value.TotalSeconds) },
            };

    /// <summary>What a ledger entry names as its cause: the caller and the request's idempotency key.</summary>
    private static string Cause(ClaimsPrincipal user, HttpContext http) =>
        $"request:{user.UserId():N}:{http.Request.Headers[IdempotentRequests<PledgesDbContext>.Header]}";
}

/// <summary>The JSON body of a pledge. <c>payment_method_id</c> is the PSP's reference to a saved card; without it, the card is saved first.</summary>
internal sealed record PledgeRequest(long AmountMinor, string? TierId, string? PaymentMethodId);

/// <summary>The JSON body of a change: the pledge's new amount and tier.</summary>
internal sealed record ChangeRequest(long AmountMinor, string? TierId);
