using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using CrowdFunding.RemoteCalls;

using Microsoft.Extensions.Logging;

using Payments.Contracts;

using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Payments.Client;

/// <summary>
/// <see cref="StartCardSetup"/> sent to Payments over HTTP, in place of the in-process handler, with the
/// same result: Payments' refusals come back as the same <see cref="Error"/> (by <c>reason</c>), a full
/// lane as 429 with its wait, and a Payments that does not answer, times out, answers 5xx or has the
/// circuit open as <see cref="ErrorKind.Unavailable"/> with a wait, which Pledges treats as it treats a
/// full lane: the pledge keeps its place and the backer asks for the card form again later. A 4xx with
/// no reason is a fault of contract or configuration, not an outage: <see cref="PaymentsRefused"/>, with
/// no wait, so Pledges keeps nothing; it is counted and logged (<see cref="RefusedCalls"/>). A call this task's limit turns away (s5-l12) gets the
/// unavailable answer at once: one fallback for every outage.
/// </summary>
public sealed partial class RemoteStartCardSetup(HttpClient http, ILogger<RemoteStartCardSetup> logger, RefusedCalls refused)
    : ICommandHandler<StartCardSetup, CardSetupStarted>
{
    /// <summary>The command's method and path, from its contract (s4-l06).</summary>
    private static readonly RemoteEndpointAttribute Endpoint = typeof(StartCardSetup).GetCustomAttribute<RemoteEndpointAttribute>()
        ?? throw new InvalidOperationException("StartCardSetup names no [RemoteEndpoint].");

    /// <summary>When to ask again after Payments did not answer: the circuit breaker's own break.</summary>
    public static readonly TimeSpan UnavailableFor = TimeSpan.FromSeconds(5);

    public static readonly Error PaymentsUnavailable =
        Error.Unavailable("payments_unavailable", "Payments did not answer; your pledge keeps its place. Ask for the card form again shortly.", UnavailableFor);

    /// <summary>
    /// Payments refused the call without a reason of its own (a binding 400, the wrong port's bare 404, the
    /// token check's 401 or 403): asking again later will not help, so there is no wait.
    /// </summary>
    public static readonly Error PaymentsRefused =
        Error.Conflict("payments_refused", "Payments refused the card setup; nothing was kept.");

    public async Task<Result<CardSetupStarted>> Handle(StartCardSetup command, CancellationToken cancellationToken)
    {
        // The request and the reply are the contract's JSON, read strictly (ContractJson, s4-l06). For one
        // release the body also carries the s4-l05 snake_case names, so a Payments task still on s4-l05
        // reads the real ids instead of binding empty ones while the services roll (CQA-1519).
        using var request = new HttpRequestMessage(new HttpMethod(Endpoint.Method), Endpoint.Path)
        {
            Content = JsonContent.Create(CardSetupWire.Both(command), options: ContractJson.Options),
        };
        // A card setup is idempotent per pledge, so the pledge is the key: a retried attempt gets the first one's setup.
        request.Headers.Add(RemoteCalls.IdempotencyKeyHeader, command.PledgeId.ToString());
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<CardSetupStarted>(await ErrorOf(response, cancellationToken));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (CardSetupWire.Read<CardSetupStarted>(body) is { } started)
            {
                return Result.Success(started);
            }

            // A reply this client cannot read is a contract fault, not an outage: refused, counted and logged.
            refused.Record("payments", nameof(StartCardSetup), response.StatusCode);
            LogUnreadable(logger, command.PledgeId);
            return Result.Failure<CardSetupStarted>(PaymentsRefused);
        }
        catch (RateLimiterRejectedException)
        {
            LogTurnedAway(logger, command.PledgeId);
            return Result.Failure<CardSetupStarted>(PaymentsUnavailable);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
        {
            LogUnavailable(logger, e, command.PledgeId);
            return Result.Failure<CardSetupStarted>(PaymentsUnavailable);
        }
    }

    /// <summary>A problem detail from Payments, back to the error it was made from.</summary>
    private async Task<Error> ErrorOf(HttpResponseMessage response, CancellationToken cancellationToken)
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
            _ when reason is null => Refused(response.StatusCode),
            HttpStatusCode.NotFound => Error.NotFound(reason, title ?? reason),
            HttpStatusCode.Conflict => Error.Conflict(reason, title ?? reason),
            HttpStatusCode.Forbidden => Error.Forbidden(reason, title ?? reason),
            _ => Error.Validation(reason, title ?? reason),
        };
    }

    private Error Refused(HttpStatusCode status)
    {
        refused.Record("payments", nameof(StartCardSetup), status);
        return PaymentsRefused;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Payments' reply to the card setup of pledge {PledgeId} is not a CardSetupStarted this client can read; nothing was kept")]
    private static partial void LogUnreadable(ILogger logger, Guid pledgeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payments did not answer the card setup of pledge {PledgeId}; the pledge keeps its place")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, Guid pledgeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "This task already has its limit of card setups in flight; pledge {PledgeId} keeps its place without a card form")]
    private static partial void LogTurnedAway(ILogger logger, Guid pledgeId);
}
