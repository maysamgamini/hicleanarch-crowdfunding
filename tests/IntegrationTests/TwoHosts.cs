using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Payments.Client;
using Payments.Host;

namespace IntegrationTests;

/// <summary>
/// Lesson s4-l05's split, in one test process: the monolith with <c>Modules:Payments:Mode = Remote</c>
/// and the Payments service, each in its own <see cref="WebApplicationFactory{TEntryPoint}"/>, on the one
/// test database and one set of topics and queues (Moto).
/// <list type="bullet">
/// <item>The monolith's HTTP client for Payments' internal port is handed the Payments host's in-memory
/// handler, through <see cref="Link"/>, which a test can take down.</item>
/// <item>Events cross between the two hosts through SNS and SQS, as in AWS: Payments.Host reads the
/// <c>payments</c> queue, the monolith the <c>monolith</c> queue (each reads only the queue of the modules
/// it runs).</item>
/// </list>
/// </summary>
public sealed class TwoHosts : IDisposable
{
    public TwoHosts(Database database, Action<IWebHostBuilder>? both = null)
    {
        var broker = database.Provision().Settings;
        Payments = PaymentsService(database, broker, both);
        Monolith = database.StartHost(broker, services => services.AddHttpClient<RemoteStartCardSetup>()
                .ConfigurePrimaryHttpMessageHandler(() => new LinkHandler(Link, Payments.Server.CreateHandler())))
            .WithWebHostBuilder(web =>
            {
                web.UseSetting("Modules:Payments:BaseAddress", "http://payments:8081");
                both?.Invoke(web);
            });

        _ = Payments.Services;
        _ = Monolith.Services;
    }

    public WebApplicationFactory<Program> Monolith { get; }

    public WebApplicationFactory<PaymentsHostEntry> Payments { get; }

    /// <summary>The network between the two: set <see cref="PaymentsLink.Down"/> and every call to Payments fails to connect.</summary>
    public PaymentsLink Link { get; } = new();

    public void Dispose()
    {
        Monolith.Dispose();
        Payments.Dispose();
    }

    /// <summary>
    /// Payments.Host on the test database and <paramref name="broker"/>'s topics and queues, with the
    /// settings the monolith's tests use. Its events' consumers run elsewhere, so each is routed Broker.
    /// </summary>
    public static WebApplicationFactory<PaymentsHostEntry> PaymentsService(Database database, IReadOnlyDictionary<string, string?> broker,
        Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<PaymentsHostEntry>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:Owner", database.Owner)
                .UseSetting("ConnectionStrings:Payments", database.Payments)
                .UseSetting("Payments:WebhookSecret", Database.WebhookSecret)
                .UseSetting("Payments:Settlement:PollEvery", "01:00:00")
                .UseSetting("Payments:Money:PollEvery", "01:00:00")
                .UseSetting("Payments:Internal:Port", "0")
                .UseSetting("Auth:ClientId", Database.ClientId)
                .UseSetting("Auth:LocalSigningKey", Database.SigningKey)
                .UseSetting("Auth:ServiceClients:0", Database.ServiceClientId)
                .UseSetting("Events:Routes:Pledges", "Broker")
                .UseSetting("Events:Routes:Campaigns", "Broker")
                .UseSetting("Events:Routes:Notifications", "Broker")
                .UseSetting("Events:Broker:Backoff:0", "00:00:01")
                .UseSetting("Events:Broker:WaitSeconds", "2")
                .UseSetting("Outbox:Publishing:RecoverAfter", "00:10:00");
            foreach (var (key, value) in broker)
            {
                web.UseSetting(key, value);
            }

            configure?.Invoke(web);
        });

    public sealed class PaymentsLink
    {
        private volatile bool _down;

        public bool Down
        {
            get => _down;
            set => _down = value;
        }
    }

    /// <summary>
    /// The network between the hosts. On a real socket, .NET's own handler puts the caller's trace context
    /// on the request (traceparent); the in-memory server's handler does not, so this does, as it would.
    /// </summary>
    private sealed class LinkHandler(PaymentsLink link, HttpMessageHandler payments) : DelegatingHandler(payments)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (link.Down)
            {
                throw new HttpRequestException("Connection refused (payments:8081)", null, System.Net.HttpStatusCode.ServiceUnavailable);
            }

            System.Diagnostics.DistributedContextPropagator.Current.Inject(System.Diagnostics.Activity.Current, request,
                static (carrier, name, value) => ((HttpRequestMessage)carrier!).Headers.TryAddWithoutValidation(name, value));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
