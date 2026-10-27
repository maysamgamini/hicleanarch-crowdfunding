using Microsoft.Extensions.Logging;

using Payments.Application;

using Polly;
using Polly.CircuitBreaker;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Configuration section <c>Payments:Breaker</c>. Failures are errors and timeouts, never declines: a
/// decline is the PSP working.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>The share of failed calls within <see cref="SamplingDuration"/> that opens the circuit.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Calls the window must have seen before the share counts, so a few failures in a quiet spell do not open it.</summary>
    public int MinimumThroughput { get; set; } = 10;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an open circuit refuses every call before one trial call is let through.</summary>
    public TimeSpan OpenFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Up to this share of <see cref="OpenFor"/> is added at random each time, so tasks that opened together do not try together.</summary>
    public double OpenJitter { get; set; } = 0.2;
}

/// <summary>The PSP is failing and the circuit is open: nothing is sent until it is tried again.</summary>
public sealed class PspCircuitOpenException : PspUnavailableException
{
    public PspCircuitOpenException()
        : base("The PSP is failing; calls are paused.")
    {
    }

    public PspCircuitOpenException(string message)
        : base(message)
    {
    }

    public PspCircuitOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One circuit for the PSP, per task, on Polly's circuit breaker (the strategy behind
/// Microsoft.Extensions.Resilience). Closed, every call goes through. When the failed share of the
/// calls in the sampling window reaches the ratio, it opens and refuses every call at once, spending
/// no budget. When the open time (with its jitter) has passed, it is half-open: one trial call goes
/// through while every other call is still refused. The trial's success closes it; its failure opens
/// it again. The port is not an HttpClient, so the standard HTTP resilience handler does not apply;
/// the pipeline wraps the port's calls directly.
/// </summary>
public sealed partial class CircuitBreaker
{
    private readonly CircuitBreakerStateProvider _state = new();
    private readonly ResiliencePipeline _pipeline;

    public CircuitBreaker(CircuitBreakerOptions options, TimeProvider clock, ILogger<CircuitBreaker> logger)
    {
        _pipeline = new ResiliencePipelineBuilder { TimeProvider = clock }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.MinimumThroughput,
                SamplingDuration = options.SamplingDuration,
                BreakDurationGenerator = _ => ValueTask.FromResult(options.OpenFor * (1 + (Random.Shared.NextDouble() * options.OpenJitter))),
                ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
                {
                    { Exception: HttpRequestException or PspUnavailableException or OperationCanceledException or TimeoutException } => true,
                    { Result: ChargeOutcome.Unknown { Reason: not ChargeOutcome.WaitingForBacker } } => true, // no answer in time
                    _ => false,
                }),
                StateProvider = _state,
                OnOpened = args =>
                {
                    LogOpened(logger, args.BreakDuration);
                    return default;
                },
            })
            .Build();
    }

    public bool IsOpen => _state.CircuitState is CircuitState.Open or CircuitState.Isolated;

    /// <summary>
    /// Sends one call through the circuit; an open circuit throws <see cref="PspCircuitOpenException"/> without
    /// sending it. A call the transport could not deliver (<see cref="HttpRequestException"/>) is counted as a
    /// failure, then given to the caller as the port's own <see cref="PspUnavailableException"/>.
    /// </summary>
    public async Task<T> Execute<T>(Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct => await send(ct), cancellationToken);
        }
        catch (BrokenCircuitException e)
        {
            throw new PspCircuitOpenException("The PSP is failing; calls are paused.", e);
        }
        catch (HttpRequestException e)
        {
            throw new PspUnavailableException("The PSP could not be reached.", e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The PSP is failing; calls pause for {OpenFor}")]
    private static partial void LogOpened(ILogger logger, TimeSpan openFor);
}

/// <summary>
/// The PSP port behind the circuit: an open circuit refuses before the budget is spent, and the
/// transport's errors leave it as the port's <see cref="PspUnavailableException"/>. It is the outermost
/// adapter, so the use cases never see an HTTP type.
/// </summary>
internal sealed class CircuitBreakingPaymentProvider(IPaymentProvider psp, CircuitBreaker breaker) : IPaymentProvider
{
    public Task<string> CreateCustomer(Guid backerId, string idempotencyKey, CancellationToken ct) => breaker.Execute(c => psp.CreateCustomer(backerId, idempotencyKey, c), ct);

    public Task<CardSetupIntent> StartCardSetup(string pspCustomerId, string idempotencyKey, CancellationToken ct) => breaker.Execute(c => psp.StartCardSetup(pspCustomerId, idempotencyKey, c), ct);

    public Task<ChargeOutcome> Charge(ChargeRequest request, string idempotencyKey, CancellationToken ct) =>
        breaker.Execute(c => psp.Charge(request, idempotencyKey, c), ct);

    public Task<ChargeOutcome> LookUp(ChargeReference reference, CancellationToken ct) => breaker.Execute(c => psp.LookUp(reference, c), ct);

    public Task<OnSessionPayment> StartOnSessionPayment(ChargeRequest request, string idempotencyKey, CancellationToken ct) =>
        breaker.Execute(c => psp.StartOnSessionPayment(request, idempotencyKey, c), ct);

    public Task<ChargeOutcome> Cancel(ChargeReference reference, CancellationToken ct) => breaker.Execute(c => psp.Cancel(reference, c), ct);

    public Task<string> Refund(string pspPaymentId, long amountMinor, string idempotencyKey, CancellationToken ct) =>
        breaker.Execute(c => psp.Refund(pspPaymentId, amountMinor, idempotencyKey, c), ct);

    public Task<string> Transfer(Guid creatorId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct) =>
        breaker.Execute(c => psp.Transfer(creatorId, amountMinor, currency, idempotencyKey, c), ct);

    public Task<BalanceReport> BalanceTransactions(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        breaker.Execute(c => psp.BalanceTransactions(from, to, c), ct);
}
