using System.Diagnostics;
using System.Net;

using CrowdFunding.RemoteCalls;

using Microsoft.Extensions.DependencyInjection;

using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace RemoteCalls.Tests;

public sealed class RemoteCallTests
{
    /// <summary>Stands in for the other service: answers with a fixed status (or waits), and counts attempts.</summary>
    private sealed class FakeService(HttpStatusCode status, TimeSpan? delay = null) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return new HttpResponseMessage(status);
        }
    }

    private sealed class PaymentsClient(HttpClient http)
    {
        public Task<HttpResponseMessage> StartCardSetup(bool withKey, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/internal/payments/card-setups");
            if (withKey)
            {
                request.Headers.Add(CrowdFunding.RemoteCalls.RemoteCalls.IdempotencyKeyHeader, "pledge-1");
            }

            return http.SendAsync(request, ct);
        }
    }

    private static PaymentsClient Client(FakeService service, RemoteCallOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddRemoteServiceClient<PaymentsClient>(new Uri("http://payments:8081"), options)
            .ConfigurePrimaryHttpMessageHandler(() => service);
        return services.BuildServiceProvider().GetRequiredService<PaymentsClient>();
    }

    private static RemoteCallOptions Fast => new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(200),
        TotalTimeout = TimeSpan.FromMilliseconds(800),
        MaxRetries = 1,
        BaseDelay = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task A_failed_call_that_carries_an_idempotency_key_is_tried_again()
    {
        var service = new FakeService(HttpStatusCode.ServiceUnavailable);

        var response = await Client(service, Fast).StartCardSetup(withKey: true, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, service.Attempts);
    }

    [Fact]
    public async Task A_failed_call_without_an_idempotency_key_is_sent_once()
    {
        var service = new FakeService(HttpStatusCode.ServiceUnavailable);

        await Client(service, Fast).StartCardSetup(withKey: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, service.Attempts);
    }

    [Fact]
    public async Task A_429_is_neither_retried_nor_counted_by_the_breaker()
    {
        var service = new FakeService(HttpStatusCode.TooManyRequests);
        var client = Client(service, Fast);

        // Far more than the breaker's minimum throughput: if 429s counted, it would open and refuse calls.
        for (var call = 0; call < 25; call++)
        {
            var response = await client.StartCardSetup(withKey: true, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        Assert.Equal(25, service.Attempts);
    }

    [Fact]
    public async Task Repeated_server_failures_open_the_breaker()
    {
        var service = new FakeService(HttpStatusCode.InternalServerError);
        var client = Client(service, Fast);

        var opened = false;
        for (var call = 0; call < 25 && !opened; call++)
        {
            try
            {
                await client.StartCardSetup(withKey: false, TestContext.Current.CancellationToken);
            }
            catch (BrokenCircuitException)
            {
                opened = true;
            }
        }

        Assert.True(opened);
    }

    [Fact]
    public async Task A_slow_service_is_cut_off_per_attempt_and_the_whole_call_stays_inside_the_total()
    {
        var service = new FakeService(HttpStatusCode.OK, delay: TimeSpan.FromSeconds(5));
        var options = Fast;
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => Client(service, options).StartCardSetup(withKey: true, TestContext.Current.CancellationToken));

        Assert.Equal(2, service.Attempts);
        Assert.True(clock.Elapsed < options.TotalTimeout + TimeSpan.FromMilliseconds(300), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Settings_whose_attempts_cannot_fit_in_the_total_are_refused()
    {
        // The plan's first sketch: three attempts of 2 s cannot fit in 5 s.
        var tooMany = new RemoteCallOptions { MaxRetries = 2 };

        Assert.Throws<InvalidOperationException>(tooMany.Validate);
        new RemoteCallOptions().Validate();
        Assert.True(new RemoteCallOptions().WorstCase <= TimeSpan.FromSeconds(5));
    }

    /// <summary>Holds every call until the test lets them go, and counts the most it held at once.</summary>
    private sealed class HeldService : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;

        public int MostInFlight { get; private set; }

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_release)
            {
                MostInFlight = Math.Max(MostInFlight, ++_inFlight);
            }

            await _release.Task.WaitAsync(cancellationToken);
            lock (_release)
            {
                _inFlight--;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Past_the_limit_a_call_is_refused_at_once_without_waiting_and_without_reaching_the_service()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new HeldService();
        var client = ClientOf(service, new RemoteCallOptions { MaxConcurrentCalls = 2, AttemptTimeout = TimeSpan.FromSeconds(2) });
        var held = new[] { client.StartCardSetup(withKey: true, ct), client.StartCardSetup(withKey: true, ct) };

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => client.StartCardSetup(withKey: true, ct));
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(200), $"refused after {clock.Elapsed}");

        service.Release();
        Assert.All(await Task.WhenAll(held), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2, service.MostInFlight);
        Assert.Equal(HttpStatusCode.OK, (await client.StartCardSetup(withKey: true, ct)).StatusCode); // room again
    }

    [Fact]
    public void A_limit_must_be_positive() =>
        Assert.Throws<InvalidOperationException>(new RemoteCallOptions { MaxConcurrentCalls = 0 }.Validate);

    private static PaymentsClient ClientOf(HttpMessageHandler service, RemoteCallOptions options)
    {
        var services = new ServiceCollection();
        services.AddRemoteServiceClient<PaymentsClient>(new Uri("http://payments:8081"), options)
            .ConfigurePrimaryHttpMessageHandler(() => service);
        return services.BuildServiceProvider().GetRequiredService<PaymentsClient>();
    }
}
