using System.Diagnostics.Metrics;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Pledges.Application;
using Pledges.Contracts;
using Pledges.Domain;
using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure.FundingCancel;

/// <summary>
/// Whether Pledges serves the creator's funding cancel. It opens once <c>check-creators</c> reports
/// no LIVE window without a creator, after the <c>record-creators</c> backfill (lesson s5-l05).
/// Pledges still runs in the old host behind the gateway's catch-all, so this is where the route opens.
/// </summary>
public sealed class FundingCancelOptions
{
    public bool Open { get; set; }
}

public static class FundingCancelModule
{
    internal static IServiceCollection AddFundingCancel(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FundingCancelOptions>().Bind(configuration.GetSection("Pledges:FundingCancel"));
        services.AddEventHandler<CampaignCreatorRecorded, RecordCreator>(PledgesModuleName.Value);
        return services
            .AddScoped<IFundingWindowCreators, EfFundingWindowCreators>()
            .AddScoped<ICommandHandler<CancelFundingAsCreator, CancelFundingAccepted>, CancelFundingAsCreatorHandler>();
    }

    internal static IEndpointRouteBuilder MapFundingCancel(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IOptions<FundingCancelOptions>>().Value.Open)
        {
            return app;
        }

        // The creator's cancel of a LIVE campaign, answered by the service that owns the deadline.
        // Campaigns follows FundingCancelled into the catalog, as it already does.
        app.MapPost("/v1/campaigns/{id}/funding/cancel", (string id, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<PledgesDbContext> once) =>
            once.Run(http, user.UserId(), new { id }, async ct =>
            {
                if (!PublicId.TryParse("cmp", id, out var campaignId))
                {
                    return Result.Failure<Reply>(PledgeErrors.CampaignNotFound);
                }

                var result = await dispatcher.Send<CancelFundingAsCreator, CancelFundingAccepted>(new CancelFundingAsCreator(campaignId, user.UserId()), ct);
                return result.IsSuccess
                    ? Result.Success(new Reply(StatusCodes.Status200OK, new { campaign_id = id, status = "CANCELLED" }))
                    : Result.Failure<Reply>(result.Errors);
            })).RequireAuthorization(Policies.Creator).WithTags("Pledges");
        return app;
    }

    /// <summary>
    /// <c>check-creators</c>: how many LIVE windows have no creator. Exit code 0 when none, so the
    /// funding cancel may open; 1 otherwise.
    /// </summary>
    public static async Task<int> CheckCreators(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var missing = await scope.ServiceProvider.GetRequiredService<IFundingWindowCreators>().OpenWithoutCreator(cancellationToken);
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FundingCancelModule));
        FundingCancelLog.CreatorsMissing(logger, missing);
        return missing == 0 ? 0 : 1;
    }
}

internal sealed class EfFundingWindowCreators(PledgesDbContext db, ILogger<EfFundingWindowCreators> logger) : IFundingWindowCreators
{
    private static readonly Meter Meter = new("CrowdFunding.Pledges");

    // Any value above zero means two sources disagree about a campaign's creator: someone looks.
    private static readonly Counter<long> Conflicting = Meter.CreateCounter<long>("pledges.creators.conflicting");

    public async Task<WindowCreator?> CreatorOf(Guid campaignId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<Guid?>($"""
            SELECT creator_id AS "Value" FROM pledges.funding_windows WHERE campaign_id = {campaignId}
            """).ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : new WindowCreator(rows[0]);
    }

    public async Task<CreatorRecorded> TryRecord(Guid campaignId, Guid creatorId, CancellationToken cancellationToken)
    {
        var filled = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows SET creator_id = {creatorId} WHERE campaign_id = {campaignId} AND creator_id IS NULL
            """, cancellationToken);
        if (filled == 1)
        {
            return CreatorRecorded.Recorded;
        }

        var stored = await CreatorOf(campaignId, cancellationToken);
        if (stored is null)
        {
            return CreatorRecorded.NoWindow;
        }

        if (stored.CreatorId == creatorId)
        {
            return CreatorRecorded.AlreadyKnown;
        }

        Conflicting.Add(1);
        FundingCancelLog.CreatorConflict(logger, campaignId, stored.CreatorId, creatorId);
        return CreatorRecorded.Different;
    }

    public Task<long> OpenWithoutCreator(CancellationToken cancellationToken) =>
        db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM pledges.funding_windows WHERE status = 'LIVE' AND creator_id IS NULL
            """).SingleAsync(cancellationToken);
}

internal static partial class FundingCancelLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "check-creators: {Missing} LIVE funding windows have no creator")]
    public static partial void CreatorsMissing(ILogger logger, long missing);

    [LoggerMessage(Level = LogLevel.Error, Message = "Campaign {CampaignId}: Pledges stores creator {Stored}, an event says {Received}; kept the stored one")]
    public static partial void CreatorConflict(ILogger logger, Guid campaignId, Guid? stored, Guid received);
}
