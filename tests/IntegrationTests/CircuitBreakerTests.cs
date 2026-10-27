using Microsoft.Extensions.Logging.Abstractions;

using Payments.Application;
using Payments.Infrastructure.Psp;

namespace IntegrationTests;

/// <summary>Lesson s3-l16, built: the PSP's circuit opens on a failure ratio and lets one trial call through when half-open.</summary>
public sealed class CircuitBreakerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CircuitBreakerOptions Options = new()
    {
        FailureRatio = 0.5,
        MinimumThroughput = 10,
        SamplingDuration = TimeSpan.FromSeconds(30),
        OpenFor = TimeSpan.FromSeconds(30),
        OpenJitter = 0.2,
    };

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static Task<string> Fails(CancellationToken ct) => Task.FromException<string>(new HttpRequestException("the PSP is down"));

    private static Task<string> Succeeds(CancellationToken ct) => Task.FromResult("ok");

    private static async Task Call(CircuitBreaker breaker, Func<CancellationToken, Task<string>> send)
    {
        try
        {
            await breaker.Execute(send, Ct);
        }
        catch (PspUnavailableException)
        {
            // a failed call, counted by the breaker
        }
    }

    [Fact]
    public async Task A_PSP_that_cannot_be_reached_is_the_ports_failure_not_the_transports()
    {
        var breaker = new CircuitBreaker(Options, new ManualClock(DateTimeOffset.UtcNow), NullLogger<CircuitBreaker>.Instance);

        var failure = await Assert.ThrowsAsync<PspUnavailableException>(() => breaker.Execute(Fails, Ct));

        Assert.IsType<HttpRequestException>(failure.InnerException);
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public async Task A_PSP_failing_every_other_call_opens_the_circuit()
    {
        var breaker = new CircuitBreaker(Options, new ManualClock(DateTimeOffset.UtcNow), NullLogger<CircuitBreaker>.Instance);

        for (var i = 0; i < 20 && !breaker.IsOpen; i++)
        {
            await Call(breaker, i % 2 == 0 ? Fails : Succeeds);
        }

        Assert.True(breaker.IsOpen);
        await Assert.ThrowsAsync<PspCircuitOpenException>(() => breaker.Execute(Succeeds, Ct));
    }

    [Fact]
    public async Task Declines_and_answers_never_open_the_circuit()
    {
        var breaker = new CircuitBreaker(Options, new ManualClock(DateTimeOffset.UtcNow), NullLogger<CircuitBreaker>.Instance);

        for (var i = 0; i < 30; i++)
        {
            await breaker.Execute(_ => Task.FromResult<ChargeOutcome>(new ChargeOutcome.Declined("insufficient_funds", "pi_1")), Ct);
        }

        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public async Task After_the_open_time_one_trial_call_reaches_a_still_failing_PSP_and_the_others_are_refused()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var breaker = new CircuitBreaker(Options, clock, NullLogger<CircuitBreaker>.Instance);
        for (var i = 0; i < 10; i++)
        {
            await Call(breaker, Fails);
        }

        Assert.True(breaker.IsOpen);
        clock.Advance(Options.OpenFor * (1 + Options.OpenJitter) + TimeSpan.FromSeconds(1));

        var reached = 0;
        var slow = new TaskCompletionSource();
        async Task<string> SlowlyFails(CancellationToken ct)
        {
            Interlocked.Increment(ref reached);
            await slow.Task;
            throw new HttpRequestException("still down");
        }

        var calls = Enumerable.Range(0, 10).Select(_ => Task.Run(() => breaker.Execute(SlowlyFails, Ct), Ct)).ToList();
        await Task.WhenAny(Task.WhenAll(calls), Task.Delay(500, Ct));
        slow.SetResult();
        var results = await Task.WhenAll(calls.Select(async c =>
        {
            try
            {
                await c;
                return "sent";
            }
            catch (PspCircuitOpenException)
            {
                return "refused";
            }
            catch (PspUnavailableException)
            {
                return "failed";
            }
        }));

        Assert.Equal(1, reached);
        Assert.Equal(1, results.Count(r => r == "failed"));
        Assert.Equal(9, results.Count(r => r == "refused"));
        Assert.True(breaker.IsOpen); // the trial failed: open again
    }
}
