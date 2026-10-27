using BuildingBlocks.Domain;
using BuildingBlocks.Infrastructure;

using Microsoft.AspNetCore.Http;

using Pledges.Application;
using Pledges.Domain;

namespace Pledges.Infrastructure.HotTier;

/// <summary>
/// Answers a pledge for a flagged tier with 409 tier_sold_out before anything touches the database:
/// no idempotency key row, no transaction, no wait on the tier's row. A refusal is never stored, so
/// answering it early changes nothing a retry would see.
/// </summary>
internal sealed class SoldOutShortCircuit(ISoldOutTiers soldOut) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<PledgeRequest>().SingleOrDefault();
        if (request?.TierId is { } tier
            && PublicId.TryParse("tir", tier, out var tierId)
            && await soldOut.IsSoldOut(tierId, context.HttpContext.RequestAborted))
        {
            return new[] { PledgeErrors.TierSoldOut }.ToProblem();
        }

        return await next(context);
    }
}
