using System.Net;
using System.Net.Http.Json;

using DotNet.Testcontainers.Configurations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Testcontainers.Toxiproxy;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l12: faults put where the network is. A Toxiproxy container (Shopify's, MIT) stands between a
/// Pledges host and what it calls: Payments' internal port, and the broker. A test turns a link off, slows it,
/// resets its connections or lets it hang, through Toxiproxy's HTTP API. Payments itself is the real
/// Payments.Host beside the test host, behind a socket on loopback (<see cref="PaymentsSocket"/>), so a
/// call crosses real TCP twice and a fault is what a client would see on ECS.
/// </summary>
public sealed class Faults : IAsyncLifetime
{
    /// <summary>The port Toxiproxy listens on, in its container, for each link.</summary>
    private static readonly Dictionary<string, int> Listen = new() { ["payments"] = 8666, ["broker"] = 8667 };

    private ToxiproxyContainer _toxiproxy = null!;
    private HttpClient _api = null!;

    public PaymentsSocket Payments { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Payments.Start();
        // Containers started from now on reach this port at host.testcontainers.internal.
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)Payments.Port);
        var builder = new ToxiproxyBuilder("ghcr.io/shopify/toxiproxy:2.12.0");
        foreach (var port in Listen.Values)
        {
            builder = builder.WithPortBinding(port, true);
        }

        _toxiproxy = builder.Build();
        await _toxiproxy.StartAsync();
        _api = new HttpClient { BaseAddress = new Uri($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(8474)}") };
        await Create("payments", $"host.testcontainers.internal:{Payments.Port}");
    }

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        await _toxiproxy.DisposeAsync();
        await Payments.DisposeAsync();
    }

    /// <summary>The address a host is given instead of the real one, for <paramref name="link"/>.</summary>
    public Uri Address(string link) => new($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(Listen[link])}");

    /// <summary>Points the broker link at <paramref name="upstream"/> (Moto, on Docker's network).</summary>
    public async Task Broker(string upstream)
    {
        await _api.DeleteAsync("/proxies/broker");
        await Create("broker", upstream);
    }

    /// <summary>Every link on, and no fault on any.</summary>
    public async Task Reset()
    {
        (await _api.PostAsync("/reset", null)).EnsureSuccessStatusCode();
        Payments.Answer = PaymentsSocket.Forward;
    }

    /// <summary>Nothing listens: a new connection is refused, an open one is closed.</summary>
    public Task Down(string link) => Enabled(link, false);

    public Task Up(string link) => Enabled(link, true);

    /// <summary>Every answer from <paramref name="link"/> arrives <paramref name="by"/> late.</summary>
    public Task Slow(string link, TimeSpan by) =>
        Toxic(link, "latency", new { latency = (int)by.TotalMilliseconds, jitter = 0 });

    /// <summary>Every connection is reset (TCP RST) as soon as data arrives.</summary>
    public Task ResetConnections(string link) => Toxic(link, "reset_peer", new { timeout = 0 });

    /// <summary>Connections open, and nothing ever comes back.</summary>
    public Task Hang(string link) => Toxic(link, "timeout", new { timeout = 0 });

    private async Task Create(string name, string upstream) =>
        (await _api.PostAsJsonAsync("/proxies", new { name, listen = $"0.0.0.0:{Listen[name]}", upstream, enabled = true })).EnsureSuccessStatusCode();

    private async Task Enabled(string link, bool enabled) =>
        (await _api.PostAsJsonAsync($"/proxies/{link}", new { enabled })).EnsureSuccessStatusCode();

    private async Task Toxic(string link, string type, object attributes) =>
        (await _api.PostAsJsonAsync($"/proxies/{link}/toxics", new { name = type, type, stream = "downstream", toxicity = 1.0, attributes })).EnsureSuccessStatusCode();
}

/// <summary>
/// Payments' internal port on a real socket: a loopback server that hands each request to the Payments
/// service beside the test host (its in-memory server), or answers in its place when a test says so.
/// It counts the requests that reach it, and the most it held at once.
/// </summary>
public sealed class PaymentsSocket : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _inFlight;
    private int _mostInFlight;
    private int _requests;

    public PaymentsSocket()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Run(Handle);
    }

    /// <summary>What answers: the default passes the request on to Payments.</summary>
    public delegate Task<HttpResponseMessage> Answering(HttpRequestMessage request, HttpMessageInvoker? payments, CancellationToken cancellationToken);

    public static readonly Answering Forward = (request, payments, ct) =>
        (payments ?? throw new InvalidOperationException("No Payments service to forward to.")).SendAsync(request, ct);

    public Answering Answer { get; set; } = Forward;

    /// <summary>The Payments service's in-memory server, set once the test host has started it.</summary>
    public HttpMessageInvoker? Target { get; set; }

    public int Port => new Uri(_app.Urls.Single()).Port;

    public int Requests => Volatile.Read(ref _requests);

    public int MostInFlight => Volatile.Read(ref _mostInFlight);

    /// <summary>Payments answers 503 with a wait, as a task that is shedding load would.</summary>
    public static Answering Unavailable(int retryAfterSeconds) => (_, _, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return Task.FromResult(response);
    };

    public void Count()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _mostInFlight, 0);
    }

    public Task Start() => _app.StartAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task Handle(HttpContext context)
    {
        Interlocked.Increment(ref _requests);
        var now = Interlocked.Increment(ref _inFlight);
        for (var most = Volatile.Read(ref _mostInFlight); now > most; most = Volatile.Read(ref _mostInFlight))
        {
            Interlocked.CompareExchange(ref _mostInFlight, now, most);
        }

        try
        {
            // As the client sent it, to Payments' internal port (http://payments:8081).
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                new Uri(PaymentsInternal, context.Request.Path + context.Request.QueryString));
            request.Content = new StreamContent(context.Request.Body);
            foreach (var (name, values) in context.Request.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
                {
                    request.Content.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
                }
            }

            request.Headers.Host = null;
            using var response = await Answer(request, Target, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (!string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers[name] = values.ToArray();
                }
            }

            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private static readonly Uri PaymentsInternal = new("http://payments:8081");
}
