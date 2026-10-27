using BuildingBlocks.Infrastructure.Identity;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(IntegrationTests.Database))]

namespace IntegrationTests;

/// <summary>One real PostgreSQL 17 for the whole test run, migrated once, as in a deploy.</summary>
public sealed class Database : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("crowdfunding")
        .Build();

    public string Owner => _container.GetConnectionString();

    public string Campaigns => new Npgsql.NpgsqlConnectionStringBuilder(Owner)
    {
        Username = "campaigns_app",
        Password = "campaigns_test",
    }.ConnectionString;

    public const string ClientId = "test-client";

    public const string SigningKey = "test-only-signing-key-0123456789abcdef";

    /// <summary>A token for a new user in the given groups, as Cognito would issue it.</summary>
    public static string Token(Guid userId, params string[] groups) => LocalTokens.Issue(SigningKey, ClientId, userId, groups);

    public static string Token(params string[] groups) => Token(Guid.NewGuid(), groups);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var host = StartHost();
        await host.Services.GetRequiredService<BuildingBlocks.Infrastructure.Persistence.DatabaseMigrator>()
            .Run(Owner, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

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

    /// <summary>A fresh host process on this database: a new host is what a restart looks like.</summary>
    public WebApplicationFactory<Program> StartHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web => web
            .UseEnvironment("Testing")
            // UseSetting, not ConfigureAppConfiguration: the host reads connection strings while it is built.
            .UseSetting("ConnectionStrings:Owner", Owner)
            .UseSetting("ConnectionStrings:Campaigns", Campaigns)
            .UseSetting("Auth:ClientId", ClientId)
            .UseSetting("Auth:LocalSigningKey", SigningKey));
}
