using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using Pledges.Domain;

namespace Pledges.Infrastructure;

/// <summary>
/// The publish gap (lesson s5-l07). <c>CampaignPublished</c> reaches Pledges through SNS and SQS, so for a
/// moment after a publish the catalog says LIVE and Pledges has no funding window. Pledges cannot tell that
/// campaign from a draft or a wrong id, so its answer stays 404 <c>campaign_not_found</c>; this filter only
/// adds the problem's <c>detail</c>, which tells a client when a retry is worth it.
/// </summary>
internal sealed class PublishGapHint : IEndpointFilter
{
    public const string Detail =
        "A campaign published a moment ago opens for pledges within a few seconds. Retry only if the catalog " +
        "(GET /v1/campaigns/{id}) shows it LIVE: at most 3 times, after 1, 2 and 4 seconds.";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        if (result is ProblemHttpResult { StatusCode: StatusCodes.Status404NotFound } problem
            && problem.ProblemDetails.Extensions.TryGetValue("reason", out var reason)
            && reason as string == PledgeErrors.CampaignNotFound.Code)
        {
            problem.ProblemDetails.Detail = Detail;
        }

        return result;
    }
}
