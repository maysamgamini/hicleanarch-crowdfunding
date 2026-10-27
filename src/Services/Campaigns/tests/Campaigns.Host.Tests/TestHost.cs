using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using CrowdFunding.Messaging;
using CrowdFunding.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Campaigns.Host.Tests.TestHost))]

namespace Campaigns.Host.Tests;

/// <summary>
/// One real PostgreSQL 17 for this service's tests, prepared by the service's own migrator, so it
/// holds the Campaigns schema and nothing of the other modules; and Moto's SNS and SQS, provisioned from
/// events/subscriptions.json, so the service reads its own queue and every event it publishes for
/// another service goes through the broker, as in AWS.
/// </summary>
public sealed class TestHost : IAsyncLifetime
{
    public const string ClientId = "local-client";

    public const string SigningKey = "test-only-signing-key-0123456789abcdef";

    private static readonly string[] Modules = ["Campaigns", "Pledges", "Payments", "Notifications"];

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17").WithDatabase("crowdfunding").Build();

    private readonly MotoBroker _broker = new();

    private IReadOnlyDictionary<string, string?> _brokerSettings = new Dictionary<string, string?>();

    public string Owner => _database.GetConnectionString();

    /// <summary>The module's own login role, which the migrator creates with this password.</summary>
    public string ModuleRole => new NpgsqlConnectionStringBuilder(Owner)
    {
        Username = "campaigns_app",
        Password = "campaigns_test",
    }.ConnectionString;

    /// <summary>A token for a new user in the given groups, as Cognito would issue it.</summary>
    public static string Token(params string[] groups) => LocalTokens.Issue(SigningKey, ClientId, Guid.NewGuid(), groups);

    /// <summary>The service in memory, with the test's settings; <paramref name="settings"/> add to them or override them.</summary>
    public WebApplicationFactory<Program> Start(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:Owner", Owner)
                .UseSetting("ConnectionStrings:Campaigns", ModuleRole)
                .UseSetting("Auth:ClientId", ClientId)
                .UseSetting("Auth:LocalSigningKey", SigningKey)
                .UseSetting("Events:Broker:WaitSeconds", "2");
            foreach (var (key, value) in _brokerSettings)
            {
                web.UseSetting(key, value);
            }

            // Every other module runs elsewhere: what this service publishes for it goes through the broker.
            foreach (var module in Modules.Where(m => m != "Campaigns"))
            {
                web.UseSetting($"Events:Routes:{module}", "Broker");
            }

            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }
        });

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_database.StartAsync(), _broker.Start());
        var plan = BrokerPlan.Load(Path.Combine(AppContext.BaseDirectory, "events", "subscriptions.json"));
        _brokerSettings = (await _broker.Provision(plan, $"t{Guid.NewGuid():N}"[..12], ["campaigns"])).Settings;
        await using var host = Start();
        await host.Services.GetRequiredService<DatabaseMigrator>().Run(Owner, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        await _database.DisposeAsync();
    }
}
