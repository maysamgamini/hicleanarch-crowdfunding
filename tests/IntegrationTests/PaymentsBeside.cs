using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Payments.Host;

namespace IntegrationTests;

/// <summary>
/// The Payments service beside one test monolith (lesson s4-l10: Payments runs only in its own service).
/// Each monolith host gets one, created and started with it and stopped with it, on the same database and
/// the same topics and queues. It takes the monolith's settings that are Payments' to read: every
/// <c>Payments:*</c> key, its connection strings (<c>ConnectionStrings:Payments*</c>), the Valkey connection, the broker's addresses and the outbox's timings. So a test
/// that sets <c>Payments:Budget:*</c> on its host sets it where Payments runs.
/// </summary>
public sealed class PaymentsBeside : IDisposable
{
    private static readonly string[] Passed = ["Payments:", "ConnectionStrings:Payments", "ConnectionStrings:Valkey", "Events:Broker:", "Outbox:"];

    public PaymentsBeside(Database database, IConfiguration monolith, IHostEnvironment environment, Action<IWebHostBuilder>? configure)
    {
        var settings = monolith.AsEnumerable()
            .Where(kv => kv.Value is not null && Passed.Any(p => kv.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                && !kv.Key.StartsWith("Events:Broker:Read:", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        settings["Events:Broker:Read:0"] = "payments";
        // The same environment as the monolith beside it: a Development pair runs both in Development.
        Host = TwoHosts.PaymentsService(database, settings, web =>
        {
            web.UseEnvironment(environment.EnvironmentName);
            configure?.Invoke(web);
        });
    }

    public WebApplicationFactory<PaymentsHostEntry> Host { get; }

    public void Dispose() => Host.Dispose();

    /// <summary>The monolith's primary handler for Payments' internal port.</summary>
    internal static HttpMessageHandler Link(TwoHosts.PaymentsLink link, PaymentsBeside payments) =>
        TwoHosts.Network(link, payments.Host.Server.CreateHandler());

    /// <summary>Starts Payments.Host when the monolith starts, so its queue is read from the first event on.</summary>
    internal sealed class StartWithMonolith(PaymentsBeside payments) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = payments.Host.Services;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>The gateway's routes (src/Gateway/appsettings.json): Payments' four public paths to Payments, the preference to Notifications, the rest to the monolith.</summary>
    internal sealed class GatewayRoutes(WebApplicationFactory<Program> monolith) : DelegatingHandler(monolith.Server.CreateHandler())
    {
        private readonly HttpMessageInvoker _payments = new(monolith.Payments().Server.CreateHandler());

        private readonly HttpMessageInvoker _notifications = new(monolith.Notifications().Host.Server.CreateHandler());

        // Campaigns' paths go to the Campaigns service (s5-l08, s5-l09).
        private readonly HttpMessageInvoker _campaigns = new(monolith.Campaigns().Host.Server.CreateHandler());

        public static bool IsCampaigns(HttpMethod method, string path) =>
            path.Trim('/').Split('/') switch
            {
                ["v1", "campaigns"] => method == HttpMethod.Post,
                ["v1", "campaigns", _] => method == HttpMethod.Get,
                ["v1", "campaigns", _, "publish" or "cancel"] => method == HttpMethod.Post,
                _ => false,
            };

        public static bool IsNotifications(HttpMethod method, string path) =>
            method == HttpMethod.Put && path.Trim('/').Split('/') is ["v1", "me", "notifications", _];

        public static bool IsPayments(HttpMethod method, string path)
        {
            var segments = path.Trim('/').Split('/');
            return (method == HttpMethod.Post && path == "/v1/webhooks/psp")
                || (segments is ["v1", "pledges", _, "payment-method"] && method == HttpMethod.Put)
                || (segments is ["v1", "pledges", _, "confirm-payment"] && method == HttpMethod.Post)
                || (segments is ["v1", "campaigns", _, "settlement"] && method == HttpMethod.Get);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            IsPayments(request.Method, request.RequestUri!.AbsolutePath) ? _payments.SendAsync(request, cancellationToken)
            : IsNotifications(request.Method, request.RequestUri!.AbsolutePath) ? _notifications.SendAsync(request, cancellationToken)
            : IsCampaigns(request.Method, request.RequestUri!.AbsolutePath) ? _campaigns.SendAsync(request, cancellationToken)
            : base.SendAsync(request, cancellationToken);
    }
}

public static class PaymentsBesideExtensions
{
    /// <summary>The Payments service beside this monolith.</summary>
    public static WebApplicationFactory<PaymentsHostEntry> Payments(this WebApplicationFactory<Program> monolith) =>
        monolith.Services.GetRequiredService<PaymentsBeside>().Host;

    /// <summary>The services of the host that runs <typeparamref name="TCommand"/>'s module: a command goes to its owner.</summary>
    public static IServiceProvider ServicesFor<TCommand>(this WebApplicationFactory<Program> monolith) =>
        typeof(TCommand).Namespace!.StartsWith("Payments.", StringComparison.Ordinal) ? monolith.Payments().Services
        : typeof(TCommand).Namespace!.StartsWith("Campaigns.", StringComparison.Ordinal) ? monolith.Campaigns().Services
        : monolith.Services;
}
