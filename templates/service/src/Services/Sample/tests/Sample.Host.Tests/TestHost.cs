using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Sample.Host.Tests.TestHost))]

namespace Sample.Host.Tests;

/// <summary>
/// One real PostgreSQL 17 for this service's tests, prepared by the service's own migrator, so it
/// holds the Sample schema and nothing of the other modules.
/// </summary>
public sealed class TestHost : IAsyncLifetime
{
    public const string ClientId = "local-client";

    public const string SigningKey = "test-only-signing-key-0123456789abcdef";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17").WithDatabase("crowdfunding").Build();

    public string Owner => _database.GetConnectionString();

    /// <summary>The module's own login role, which the migrator creates with this password.</summary>
    public string ModuleRole => new NpgsqlConnectionStringBuilder(Owner)
    {
        Username = "sample_app",
        Password = "sample_test",
    }.ConnectionString;

    /// <summary>A token for a new user in the given groups, as Cognito would issue it.</summary>
    public static string Token(params string[] groups) => LocalTokens.Issue(SigningKey, ClientId, Guid.NewGuid(), groups);

    /// <summary>The service in memory, with the test's settings; <paramref name="settings"/> add to them or override them.</summary>
    public WebApplicationFactory<Program> Start(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:Owner", Owner)
                .UseSetting("ConnectionStrings:Sample", ModuleRole)
                .UseSetting("Auth:ClientId", ClientId)
                .UseSetting("Auth:LocalSigningKey", SigningKey);
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }
        });

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();
        await using var host = Start();
        await host.Services.GetRequiredService<DatabaseMigrator>().Run(Owner, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
