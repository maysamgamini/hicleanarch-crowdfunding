using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace CrowdFunding.RemoteCalls;

/// <summary>
/// The HTTP client a module uses to call another service. One pipeline, outermost first: a total
/// timeout, retries, a circuit breaker, and a timeout per attempt.
/// <list type="bullet">
/// <item>Only a request that carries an <c>Idempotency-Key</c> is retried. The header is the caller's
/// statement that the command is idempotent at the receiver by that key: the receiver does not read
/// it and stores no reply, so a retry runs the command again from the start, and only a command that
/// ends the same way the second time may carry it. <c>StartCardSetup</c> does, per pledge: the PSP call
/// carries the pledge's key and a pledge has one setup row. Anything else is sent once.</item>
/// <item>A 429 is the other service's deliberate back-pressure: it is never retried here and never
/// counts as a failure for the breaker; the caller passes its Retry-After on.</item>
/// </list>
/// </summary>
public static class RemoteCalls
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IHttpClientBuilder AddRemoteServiceClient<TClient>(
        this IServiceCollection services, Uri baseAddress, RemoteCallOptions? options = null)
        where TClient : class
    {
        var o = options ?? new RemoteCallOptions();
        o.Validate();
        services.TryAddSingleton(sp => new RefusedCalls(
            sp.GetService<TimeProvider>() ?? TimeProvider.System, sp.GetRequiredService<ILogger<RefusedCalls>>()));

        var client = services.AddHttpClient<TClient>(http =>
        {
            http.BaseAddress = baseAddress;
            // The pipeline owns the timeouts; HttpClient's own 100 s must not cut in first.
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        client.AddResilienceHandler("remote-call", pipeline =>
        {
            pipeline.AddTimeout(o.TotalTimeout);
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = o.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = o.BaseDelay,
                // A Retry-After from the server would override the planned wait; the total timeout still rules.
                ShouldRetryAfterHeader = false,
                ShouldHandle = args => ValueTask.FromResult(
                    CarriesIdempotencyKey(args.Context) && IsTransientFailure(args.Outcome)),
            });
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(5),
                ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome)),
            });
            pipeline.AddTimeout(o.AttemptTimeout);
        });

        return client;
    }

    private static bool CarriesIdempotencyKey(ResilienceContext context) =>
        context.GetRequestMessage()?.Headers.Contains(IdempotencyKeyHeader) == true;

    /// <summary>A failure worth another try: no answer, a timeout, or a 5xx/408. Never a 429 or another 4xx.</summary>
    internal static bool IsTransientFailure(Outcome<HttpResponseMessage> outcome) => outcome switch
    {
        { Exception: HttpRequestException or TimeoutRejectedException } => true,
        { Result: { } response } => response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500,
        _ => false,
    };
}
