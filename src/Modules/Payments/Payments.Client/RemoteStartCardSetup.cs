using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using CrowdFunding.RemoteCalls;

using Microsoft.Extensions.Logging;

using Payments.Contracts;

using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Payments.Client;

/// <summary>
/// <see cref="StartCardSetup"/> sent to Payments over HTTP, in place of the in-process handler, with the
/// same result: Payments' refusals come back as the same <see cref="Error"/> (by <c>reason</c>), a full
/// lane as 429 with its wait, and a Payments that does not answer, times out or has the circuit open
/// as <see cref="ErrorKind.Unavailable"/> with a wait, which Pledges treats as it treats a full lane:
/// the pledge keeps its place and the backer asks for the card form again later.
/// </summary>
public sealed partial class RemoteStartCardSetup(HttpClient http, ILogger<RemoteStartCardSetup> logger)
    : ICommandHandler<StartCardSetup, CardSetupStarted>
{
    /// <summary>The services' JSON on the wire: snake_case, as every endpoint of the API.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>When to ask again after Payments did not answer: the circuit breaker's own break.</summary>
    public static readonly TimeSpan UnavailableFor = TimeSpan.FromSeconds(5);

    public static readonly Error PaymentsUnavailable =
        Error.Unavailable("payments_unavailable", "Payments did not answer; your pledge keeps its place. Ask for the card form again shortly.", UnavailableFor);

    public async Task<Result<CardSetupStarted>> Handle(StartCardSetup command, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PaymentsInternalEndpoints.StartCardSetup)
        {
            Content = JsonContent.Create(command, options: Json),
        };
        // A card setup is idempotent per pledge, so the pledge is the key: a retried attempt gets the first one's setup.
        request.Headers.Add(RemoteCalls.IdempotencyKeyHeader, command.PledgeId.ToString());
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? Result.Success((await response.Content.ReadFromJsonAsync<CardSetupStarted>(Json, cancellationToken))!)
                : Result.Failure<CardSetupStarted>(await ErrorOf(response, cancellationToken));
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
        {
            LogUnavailable(logger, e, command.PledgeId);
            return Result.Failure<CardSetupStarted>(PaymentsUnavailable);
        }
    }

    /// <summary>A problem detail from Payments, back to the error it was made from.</summary>
    private static async Task<Error> ErrorOf(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? reason = null, title = null;
        try
        {
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            reason = problem.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            title = problem.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            // not a problem detail: a proxy's page, for example
        }

        var wait = response.Headers.RetryAfter?.Delta;
        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => Error.TooManyRequests(reason ?? "too_many_requests", title ?? "Try again shortly.", wait ?? TimeSpan.FromSeconds(1)),
            >= HttpStatusCode.InternalServerError => PaymentsUnavailable with { RetryAfter = wait ?? UnavailableFor },
            _ when reason is null => PaymentsUnavailable,
            HttpStatusCode.NotFound => Error.NotFound(reason, title ?? reason),
            HttpStatusCode.Conflict => Error.Conflict(reason, title ?? reason),
            HttpStatusCode.Forbidden => Error.Forbidden(reason, title ?? reason),
            _ => Error.Validation(reason, title ?? reason),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payments did not answer the card setup of pledge {PledgeId}; the pledge keeps its place")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, Guid pledgeId);
}
