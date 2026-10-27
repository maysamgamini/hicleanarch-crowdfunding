using Microsoft.Extensions.Logging;

using Payments.Application;

namespace Payments.Infrastructure.Psp;

/// <summary>Configuration section <c>Payments:Breaker</c>.</summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>Failures in a row that open the circuit: errors and timeouts, never declines.</summary>
    public int FailuresToOpen { get; set; } = 5;

    /// <summary>How long an open circuit refuses every call before one is let through to try.</summary>
    public TimeSpan OpenFor { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>The PSP is failing and the circuit is open: nothing is sent until it is tried again.</summary>
public sealed class PspCircuitOpenException : HttpRequestException
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
/// One circuit for the PSP, per task. Closed, every call goes through; after enough failures in a
/// row it opens and refuses every call at once, spending no budget. When the open time has passed,
/// calls go through again, and one more failure opens it at once (half-open); a success closes it.
/// </summary>
public sealed partial class CircuitBreaker(CircuitBreakerOptions options, TimeProvider clock, ILogger<CircuitBreaker> logger)
{
    private readonly Lock _lock = new();
    private int _failures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return clock.GetUtcNow() < _openUntil;
            }
        }
    }

    public void ThrowIfOpen()
    {
        if (IsOpen)
        {
            throw new PspCircuitOpenException();
        }
    }

    public void Succeeded()
    {
        lock (_lock)
        {
            _failures = 0;
        }
    }

    public void Failed()
    {
        lock (_lock)
        {
            _failures++;
            if (_failures >= options.FailuresToOpen)
            {
                _openUntil = clock.GetUtcNow() + options.OpenFor;
                LogOpened(logger, _failures, options.OpenFor);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The PSP failed {Failures} times in a row; calls pause for {OpenFor}")]
    private static partial void LogOpened(ILogger logger, int failures, TimeSpan openFor);
}

/// <summary>The PSP port behind the circuit: an open circuit refuses before the budget is spent.</summary>
internal sealed class CircuitBreakingPaymentProvider(IPaymentProvider psp, CircuitBreaker breaker) : IPaymentProvider
{
    public Task<string> CreateCustomer(Guid backerId, CancellationToken ct) => Guarded(c => psp.CreateCustomer(backerId, c), _ => true, ct);

    public Task<CardSetupIntent> StartCardSetup(string pspCustomerId, CancellationToken ct) => Guarded(c => psp.StartCardSetup(pspCustomerId, c), _ => true, ct);

    public Task<ChargeOutcome> Charge(ChargeRequest request, string idempotencyKey, CancellationToken ct) =>
        Guarded(c => psp.Charge(request, idempotencyKey, c), Answered, ct);

    public Task<ChargeOutcome> LookUp(ChargeReference reference, CancellationToken ct) =>
        Guarded(c => psp.LookUp(reference, c), Answered, ct);

    /// <summary>A decline is the PSP working; a timeout is not.</summary>
    private static bool Answered(ChargeOutcome outcome) => outcome is not ChargeOutcome.Unknown;

    private async Task<T> Guarded<T>(Func<CancellationToken, Task<T>> send, Func<T, bool> answered, CancellationToken ct)
    {
        breaker.ThrowIfOpen();
        try
        {
            var result = await send(ct);
            if (answered(result))
            {
                breaker.Succeeded();
            }
            else
            {
                breaker.Failed();
            }

            return result;
        }
        catch (HttpRequestException)
        {
            breaker.Failed();
            throw;
        }
    }
}
