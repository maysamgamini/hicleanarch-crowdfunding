using BuildingBlocks.Domain;

using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Infrastructure;

/// <summary>Turns refusals into HTTP problem details: 422 for bad input, 403, 404, 409, each with a reason.</summary>
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
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        return Results.Problem(
            statusCode: status,
            title: first.Message,
            extensions: new Dictionary<string, object?>
            {
                ["reason"] = first.Code,
                ["errors"] = errors.Select(e => new { code = e.Code, message = e.Message }).ToArray(),
            });
    }
}
