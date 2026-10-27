using BuildingBlocks.Domain;

using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Infrastructure;

/// <summary>
/// Turns refusals into HTTP problem details: 422 for bad input, 403, 404, 409, 429, 503, each with a
/// reason. A refusal that carries <see cref="Error.RetryAfter"/> also sends <c>Retry-After</c> in seconds.
/// </summary>
public static class ErrorResults
{
    public static IResult ToProblem(this IReadOnlyList<Error> errors, int? statusCode = null)
    {
        var first = errors[0];
        var status = statusCode ?? first.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
            ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        var problem = Results.Problem(
            statusCode: status,
            title: first.Message,
            extensions: new Dictionary<string, object?>
            {
                ["reason"] = first.Code,
                ["errors"] = errors.Select(e => new { code = e.Code, message = e.Message }).ToArray(),
            });
        return first.RetryAfter is { } wait ? new WithRetryAfter(problem, wait) : problem;
    }

    private sealed class WithRetryAfter(IResult inner, TimeSpan wait) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = ((long)Math.Ceiling(Math.Max(wait.TotalSeconds, 1))).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
