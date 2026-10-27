using BuildingBlocks.Infrastructure.Identity;

using CrowdFunding.Messaging;
using CrowdFunding.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(IntegrationTests.Database))]

namespace IntegrationTests;

/// <summary>
/// One real PostgreSQL 17 for the whole test run, migrated once, as in a deploy, and one Moto (SNS and
/// SQS): from s4-l02 every host routes Payments' events, in and out, through the broker.
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

    public const string SigningKey = "test-only-signing-key-0123456789abcdef";

    /// <summary>A token for a new user in the given groups, as Cognito would issue it.</summary>
    public static string Token(Guid userId, params string[] groups) => LocalTokens.Issue(SigningKey, ClientId, userId, groups);

    public static string Token(params string[] groups) => Token(Guid.NewGuid(), groups);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_container.StartAsync(), Broker.Start());
        await using var host = StartHost(broker: false);
        await host.Services.GetRequiredService<BuildingBlocks.Infrastructure.Persistence.DatabaseMigrator>()
            .Run(Owner, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Broker.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>A client of the host, signed in with the token if one is given, which sends a fresh
    /// Idempotency-Key with every change unless the test sets one.</summary>
    public static HttpClient Client(WebApplicationFactory<Program> host, string? token = null)
    {
        var client = host.CreateDefaultClient(new FreshIdempotencyKeys());
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
    /// A fresh host process on this database: a new host is what a restart looks like. With
    /// <paramref name="broker"/>, the host gets topics and queues of its own (what Terraform builds, under a
    /// fresh prefix), so no other test's host reads its messages, and Payments' routes are on <c>Broker</c>.
    /// </summary>
    public WebApplicationFactory<Program> StartHost(Action<IServiceCollection>? services = null, bool broker = true) =>
        StartHost(broker ? Provision().Settings : null, services);

    /// <summary>Topics and queues for one host, the monolith of s4-l02, which reads both queues.</summary>
    public MotoBroker.Provisioned Provision() =>
        Broker.Provision(Plan, $"t{Guid.NewGuid():N}"[..12], Plan.Services.Keys).GetAwaiter().GetResult();

    /// <summary>
    /// A host on <paramref name="broker"/>'s topics and queues (from <see cref="Provision"/>), or with every event
    /// in process. With <paramref name="brokerRoutes"/> false, the broker is there but every route is
    /// <c>InProcess</c>: a rollback, with rows and messages still owed to the broker.
    /// </summary>
    public WebApplicationFactory<Program> StartHost(IReadOnlyDictionary<string, string?>? broker, Action<IServiceCollection>? services = null,
        bool brokerRoutes = true, string? recoverAfter = null) =>
        WithBroker(Configure(new WebApplicationFactory<Program>(), services), broker, brokerRoutes, recoverAfter);

    private static WebApplicationFactory<Program> WithBroker(WebApplicationFactory<Program> host, IReadOnlyDictionary<string, string?>? broker,
        bool brokerRoutes, string? recoverAfter)
    {
        if (broker is null)
        {
            return host;
        }

        return host.WithWebHostBuilder(web =>
        {
            foreach (var (key, value) in broker)
            {
                web.UseSetting(key, value);
            }

            // A failed message comes back after a second, not 30: the tests wait for retries.
            web.UseSetting("Events:Broker:Backoff:0", "00:00:01")
                .UseSetting("Events:Broker:WaitSeconds", "2")
                // Every test's host shares the database, and many stop within seconds: a recovery pass cut
                // short leaves its claims for two minutes. Hosts recover only what a test left for them.
                .UseSetting("Outbox:Publishing:RecoverAfter", recoverAfter ?? "00:10:00");
            if (brokerRoutes)
            {
                web.UseSetting("Events:Routes:Payments", "Broker")
                    .UseSetting("Events:Producers:Payments:Pledges", "Broker")
                    .UseSetting("Events:Producers:Payments:Campaigns", "Broker")
                    .UseSetting("Events:Producers:Payments:Notifications", "Broker");
            }
        });
    }

    private WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> factory, Action<IServiceCollection>? services) =>
        factory.WithWebHostBuilder(web => web
            .ConfigureTestServices(s => services?.Invoke(s))
            .UseEnvironment("Testing")
            // UseSetting, not ConfigureAppConfiguration: the host reads connection strings while it is built.
            .UseSetting("ConnectionStrings:Owner", Owner)
            .UseSetting("ConnectionStrings:Campaigns", Campaigns)
            .UseSetting("ConnectionStrings:Pledges", Pledges)
            .UseSetting("ConnectionStrings:PledgesJobs", PledgesJobs)
            .UseSetting("ConnectionStrings:Payments", Payments)
            .UseSetting("ConnectionStrings:Notifications", Notifications)
            .UseSetting("Payments:WebhookSecret", WebhookSecret)
            // Tests drive settlement passes themselves; the background worker waits an hour.
            .UseSetting("Payments:Settlement:PollEvery", "01:00:00")
            .UseSetting("Payments:Money:PollEvery", "01:00:00")
            .UseSetting("Pledges:CloseEvery", "01:00:00")
            .UseSetting("Auth:ClientId", ClientId)
            .UseSetting("Auth:LocalSigningKey", SigningKey));

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
