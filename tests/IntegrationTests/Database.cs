extern alias CampaignsService;
extern alias NotificationsService;

using BuildingBlocks.Infrastructure.Identity;

using CrowdFunding.Messaging;
using CrowdFunding.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Payments.Client;

using Testcontainers.PostgreSql;

using CampaignsHost = CampaignsService::Program;
using NotificationsHost = NotificationsService::Program;

[assembly: AssemblyFixture(typeof(IntegrationTests.Database))]

namespace IntegrationTests;

/// <summary>
/// One real PostgreSQL 17 for the whole test run, migrated once, as in a deploy, and one Moto (SNS and
/// SQS): from s4-l02 every host routes Payments' events, in and out, through the broker, and from s5-l07
/// the events between Campaigns and the other modules.
/// </summary>
public sealed class Database : IAsyncLifetime
{
    // Every test class starts a host, and every host keeps a connection pool per module role: more
    // than PostgreSQL's default of 100 connections when the classes run in parallel.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("crowdfunding")
        .WithCommand("-c", "max_connections=500")
        .Build();

    public string Owner => _container.GetConnectionString();

    public MotoBroker Broker { get; } = new();

    private static readonly BrokerPlan Plan = BrokerPlan.Load(Path.Combine(AppContext.BaseDirectory, "events", "subscriptions.json"));

    /// <summary>
    /// The emails every host of the run sends. The hosts share one database, so any host's email
    /// worker may send an email another host's handler recorded, as any task's would in AWS: a test
    /// looks for its own recipients here, whichever host sent them.
    /// </summary>
    public static Notifications.InMemoryEmailSender Mail { get; } = new();

    public string Campaigns => new Npgsql.NpgsqlConnectionStringBuilder(Owner)
    {
        Username = "campaigns_app",
        Password = "campaigns_test",
    }.ConnectionString;

    public string Pledges => new Npgsql.NpgsqlConnectionStringBuilder(Owner)
    {
        Username = "pledges_app",
        Password = "pledges_test",
    }.ConnectionString;

    public string PledgesJobs => new Npgsql.NpgsqlConnectionStringBuilder(Owner)
    {
        Username = "pledges_jobs",
        Password = "pledges_jobs_test",
    }.ConnectionString;

    public string Payments => new Npgsql.NpgsqlConnectionStringBuilder(Owner)
    {
        Username = "payments_app",
        Password = "payments_test",
    }.ConnectionString;

    public string Notifications => new Npgsql.NpgsqlConnectionStringBuilder(Owner)
    {
        Username = "notifications_app",
        Password = "notifications_test",
    }.ConnectionString;

    public const string WebhookSecret = "whsec_test_only_0123456789abcdef";

    public const string ClientId = "test-client";

    /// <summary>The monolith's app client for service tokens (s4-l04), the one Payments lists.</summary>
    public const string ServiceClientId = "test-monolith";

    public const string SigningKey = "test-only-signing-key-0123456789abcdef";

    /// <summary>A token for a new user in the given groups, as Cognito would issue it.</summary>
    public static string Token(Guid userId, params string[] groups) => LocalTokens.Issue(SigningKey, ClientId, userId, groups);

    public static string Token(params string[] groups) => Token(Guid.NewGuid(), groups);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_container.StartAsync(), Broker.Start());
        // Two migrate steps, as two deploys run them (s4-l10): the monolith's prepares its modules, and
        // Payments.Host's alone prepares Payments' schema (here on the same server; in AWS on its own cluster).
        await using var host = StartHost();
        await host.Services.GetRequiredService<BuildingBlocks.Infrastructure.Persistence.DatabaseMigrator>()
            .Run(Owner, CancellationToken.None);
        await host.Payments().Services.GetRequiredService<BuildingBlocks.Infrastructure.Persistence.DatabaseMigrator>()
            .Run(Owner, CancellationToken.None);
        // Notifications (s5-l04) and Campaigns (s5-l09) run only in their own services: each deploy migrates its schema.
        await host.Notifications().Services.GetRequiredService<BuildingBlocks.Infrastructure.Persistence.DatabaseMigrator>()
            .Run(Owner, CancellationToken.None);
        await host.Campaigns().Services.GetRequiredService<BuildingBlocks.Infrastructure.Persistence.DatabaseMigrator>()
            .Run(Owner, CancellationToken.None);

        // A role's settings apply to sessions that log in after them. This host's jobs opened pooled
        // sessions before the migrator set pledges_app's time limit, and the time fence refuses a
        // pledge on such a session; in a deploy, migrate runs before any serving task starts. Every
        // test host logs in afresh, as a new task would.
        Npgsql.NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync()
    {
        await Broker.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Runs a program inside the database's container, such as pg_dump.</summary>
    public Task<DotNet.Testcontainers.Containers.ExecResult> Exec(IList<string> command, CancellationToken ct) => _container.ExecAsync(command, ct);

    /// <summary>A client of the host, signed in with the token if one is given, which sends a fresh
    /// Idempotency-Key with every change unless the test sets one.</summary>
    public static HttpClient Client(WebApplicationFactory<Program> host, string? token = null)
    {
        // Through the gateway's routes: Payments' public paths to the Payments service, the notification
        // preference to the Notifications service, the rest to the monolith.
        var client = new HttpClient(new FreshIdempotencyKeys { InnerHandler = new PaymentsBeside.GatewayRoutes(host) }) { BaseAddress = host.Server.BaseAddress };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private sealed class FreshIdempotencyKeys : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get && !request.Headers.Contains("Idempotency-Key"))
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A fresh monolith on this database, with a Payments service of its own beside it (lesson s4-l10:
    /// Payments runs nowhere else): a new pair is what a restart looks like. The two get topics and queues
    /// of their own (what OpenTofu builds, under a fresh prefix), so no other test's hosts read their
    /// messages. Every route to another service is on <c>Broker</c>. <see cref="PaymentsBeside"/> says how the
    /// pair is wired; <c>host.Payments()</c>, <c>host.Notifications()</c> and <c>host.Campaigns()</c> are the services.
    /// <paramref name="payments"/> and <paramref name="campaigns"/> configure those services (their services, or settings they alone read).
    /// </summary>
    public WebApplicationFactory<Program> StartHost(Action<IServiceCollection>? services = null, Action<IWebHostBuilder>? payments = null,
        Action<IWebHostBuilder>? campaigns = null) =>
        StartHost(Provision().Settings, services, payments: payments, campaigns: campaigns);

    /// <summary>Topics and queues for one monolith and its services: each reads only its own service's queue.</summary>
    public MotoBroker.Provisioned Provision() =>
        // The monolith reads its own queue; Payments.Host, Notifications.Host and Campaigns.Host beside it read theirs.
        Broker.Provision(Plan, $"t{Guid.NewGuid():N}"[..12], ["monolith"]).GetAwaiter().GetResult();

    /// <summary>A monolith and its services on <paramref name="broker"/>'s topics and queues (from <see cref="Provision"/>).</summary>
    public WebApplicationFactory<Program> StartHost(IReadOnlyDictionary<string, string?> broker, Action<IServiceCollection>? services = null,
        string? recoverAfter = null, Action<IWebHostBuilder>? payments = null, Action<IWebHostBuilder>? campaigns = null) =>
        Configure(new WebApplicationFactory<Program>(), services, broker, recoverAfter ?? "00:10:00", payments, campaigns);

    /// <summary>
    /// One monolith started directly, with no Events:Broker section and no Payments service beside it: what
    /// a deploy that lost the broker's settings would start (CQA-1504's refusal). It is not meant to serve.
    /// </summary>
    public WebApplicationFactory<Program> StartMonolithWithoutBroker() =>
        Configure(new WebApplicationFactory<Program>(), null, null, "00:10:00", null, null);

    private WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> factory, Action<IServiceCollection>? services,
        IReadOnlyDictionary<string, string?>? broker, string recoverAfter, Action<IWebHostBuilder>? payments, Action<IWebHostBuilder>? campaigns) =>
        factory.WithWebHostBuilder(web =>
        {
            web.ConfigureTestServices(s =>
                {
                    // Payments.Host beside this monolith, started with it, and the network between the two:
                    // StartCardSetup goes over HTTP to its internal port (s4-l03, s4-l05).
                    if (broker is not null)
                    {
                        s.AddSingleton(new TwoHosts.PaymentsLink());
                        s.AddSingleton(sp => new PaymentsBeside(this, sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                            sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>(), payments));
                        s.AddHostedService(sp => new PaymentsBeside.StartWithMonolith(sp.GetRequiredService<PaymentsBeside>()));
                        s.AddHttpClient<RemoteStartCardSetup>().ConfigurePrimaryHttpMessageHandler(sp =>
                            PaymentsBeside.Link(sp.GetRequiredService<TwoHosts.PaymentsLink>(), sp.GetRequiredService<PaymentsBeside>()));

                        // And the Notifications service (s5-l04), on the same database and broker, reading its own queue.
                        s.AddSingleton(new NotificationsBeside(StartNotifications(broker)));
                        s.AddHostedService(sp => sp.GetRequiredService<NotificationsBeside>());

                        // And the Campaigns service (s5-l09: Campaigns runs nowhere else), reading the campaigns queue,
                        // with the monolith's settings that are Campaigns' to read.
                        s.AddSingleton(sp => new CampaignsBeside(StartCampaigns(broker, CampaignsBeside.SettingsFrom(
                            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()), campaigns)));
                        s.AddHostedService(sp => sp.GetRequiredService<CampaignsBeside>());
                    }

                    s.AddSingleton(Mail);
                    services?.Invoke(s);
                })
                .UseEnvironment("Testing")
                .UseSetting("Modules:Payments:BaseAddress", "http://payments:8081")
                .UseSetting("ServiceIdentity:ClientId", ServiceClientId)
                // UseSetting, not ConfigureAppConfiguration: the host reads connection strings while it is built.
                .UseSetting("ConnectionStrings:Owner", Owner)
                .UseSetting("ConnectionStrings:Pledges", Pledges)
                .UseSetting("ConnectionStrings:PledgesJobs", PledgesJobs)
                .UseSetting("Pledges:CloseEvery", "01:00:00")
                // The creator's LIVE cancel is Pledges' (s5-l05), and the old path's LIVE branch is gone (s5-l08).
                .UseSetting("Pledges:FundingCancel:Open", "true")
                .UseSetting("Auth:ClientId", ClientId)
                .UseSetting("Auth:LocalSigningKey", SigningKey);
            if (broker is null)
            {
                return;
            }

            foreach (var (key, value) in broker)
            {
                web.UseSetting(key, value);
            }

            // A failed message comes back after a second, not 30: the tests wait for retries.
            web.UseSetting("Events:Broker:Backoff:0", "00:00:01")
                .UseSetting("Events:Broker:WaitSeconds", "2")
                // Every test's host shares the database, and many stop within seconds: a recovery pass cut
                // short leaves its claims for two minutes. Hosts recover only what a test left for them.
                .UseSetting("Outbox:Publishing:RecoverAfter", recoverAfter)
                // Payments runs elsewhere: its consumers and its events go through the broker.
                .UseSetting("Events:Routes:Payments", "Broker")
                .UseSetting("Events:Producers:Payments:Pledges", "Broker")
                .UseSetting("Events:Producers:Payments:Campaigns", "Broker")
                .UseSetting("Events:Producers:Payments:Notifications", "Broker")
                // Notifications (s5-l04) and Campaigns (s5-l09) run only in their own services.
                .UseSetting("Events:Routes:Notifications", "Broker")
                .UseSetting("Events:Routes:Campaigns", "Broker");
        });

    /// <summary>
    /// The Notifications service on this database, and on <paramref name="broker"/>'s topics and queues if
    /// given, reading only the notifications queue. It sends into <see cref="Mail"/>.
    /// </summary>
    public WebApplicationFactory<NotificationsHost> StartNotifications(IReadOnlyDictionary<string, string?>? broker,
        Action<IServiceCollection>? services = null) =>
        new WebApplicationFactory<NotificationsHost>().WithWebHostBuilder(web =>
        {
            web.ConfigureTestServices(s =>
                {
                    s.AddSingleton(Mail);
                    services?.Invoke(s);
                })
                .UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:Owner", Owner)
                .UseSetting("ConnectionStrings:Notifications", Notifications)
                // The email worker looks for new rows often, so a test waits for an email briefly.
                .UseSetting("Notifications:PollEvery", "00:00:00.200")
                .UseSetting("Auth:ClientId", ClientId)
                .UseSetting("Auth:LocalSigningKey", SigningKey);
            if (broker is null)
            {
                return;
            }

            foreach (var (key, value) in broker.Where(b => !b.Key.StartsWith("Events:Broker:Read:", StringComparison.OrdinalIgnoreCase)))
            {
                web.UseSetting(key, value);
            }

            web.UseSetting("Events:Broker:Read:0", "notifications")
                .UseSetting("Events:Routes:Notifications", "Broker")
                .UseSetting("Events:Broker:Backoff:0", "00:00:01")
                .UseSetting("Events:Broker:WaitSeconds", "2")
                .UseSetting("Outbox:Publishing:RecoverAfter", "00:10:00");
        });

    /// <summary>
    /// The Campaigns service (lesson s5-l08) on this database and <paramref name="broker"/>'s topics and queues,
    /// reading only the campaigns queue; every other module's events go through the broker.
    /// </summary>
    public WebApplicationFactory<CampaignsHost> StartCampaigns(IReadOnlyDictionary<string, string?> broker,
        IReadOnlyDictionary<string, string?>? settings = null, Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<CampaignsHost>().WithWebHostBuilder(web =>
        {
            web
                .UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:Owner", Owner)
                .UseSetting("ConnectionStrings:Campaigns", Campaigns)
                .UseSetting("Auth:ClientId", ClientId)
                .UseSetting("Auth:LocalSigningKey", SigningKey);
            foreach (var (key, value) in broker.Where(b => !b.Key.StartsWith("Events:Broker:Read:", StringComparison.OrdinalIgnoreCase)))
            {
                web.UseSetting(key, value);
            }

            web.UseSetting("Events:Broker:Read:0", "campaigns")
                .UseSetting("Events:Routes:Pledges", "Broker")
                .UseSetting("Events:Routes:Payments", "Broker")
                .UseSetting("Events:Routes:Notifications", "Broker")
                .UseSetting("Events:Broker:Backoff:0", "00:00:01")
                .UseSetting("Events:Broker:WaitSeconds", "2")
                .UseSetting("Outbox:Publishing:RecoverAfter", "00:10:00");
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                web.UseSetting(key, value);
            }

            configure?.Invoke(web);
        });

    /// <summary>How many rows of a table have the given value in a column, read as the owner.</summary>
    public async Task<long> Count(string table, string column, string value)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Owner);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {column} = @value", connection);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Runs SQL as the owner, for tests that must set up what the API cannot.</summary>
    public async Task Execute(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Owner);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
