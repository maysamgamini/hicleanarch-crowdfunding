using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Notifications.Host.Tests;

/// <summary>The Notifications service starts on its own, answers its health checks, and composes its module alone.</summary>
public sealed class HostTests(TestHost service)
{
    /// <summary>One endpoint of each module, to show which modules a host serves.</summary>
    private static readonly (string Module, HttpMethod Method, string Path)[] OneEndpointPerModule =
    [
        ("Campaigns", HttpMethod.Get, $"/v1/campaigns/cmp_{Guid.NewGuid():N}"),
        ("Pledges", HttpMethod.Get, $"/v1/campaigns/cmp_{Guid.NewGuid():N}/totals"),
        ("Payments", HttpMethod.Post, "/v1/webhooks/psp"),
        ("Notifications", HttpMethod.Put, "/v1/me/notifications/campaign_updates"),
    ];

    private static readonly string[] ModuleSchemas = ["campaigns", "pledges", "payments", "notifications"];

    [Fact]
    public async Task It_is_live_and_ready_with_its_own_database_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = service.Start();
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", ct)).StatusCode);
        var ready = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ready", JsonDocument.Parse(await ready.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("status").GetString());

        // The ready check covers the databases the composed modules registered: this module's alone.
        Assert.Equal("Notifications", Assert.Single(host.Services.GetServices<ModuleDatabase>()).Module);
    }

    [Fact]
    public async Task Its_migration_prepares_its_own_schema_and_no_other()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(service.Owner);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT schema_name FROM information_schema.schemata WHERE schema_name = ANY(@schemas)", connection);
        command.Parameters.AddWithValue("schemas", ModuleSchemas);
        var schemas = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                schemas.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["notifications"], schemas);
    }

    [Fact]
    public async Task It_serves_none_of_the_other_modules_endpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = service.Start();
        var client = host.CreateClient();
        // Signed in, so that a path no endpoint serves answers 404 rather than the fallback policy's 401.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestHost.Token(Roles.Backer, Roles.Creator));

        foreach (var (module, method, path) in OneEndpointPerModule.Where(e => e.Module != "Notifications"))
        {
            using var request = new HttpRequestMessage(method, path) { Content = new StringContent("{}") };
            var reply = await client.SendAsync(request, ct);
            Assert.True(reply.StatusCode == HttpStatusCode.NotFound, $"{method} {path} ({module}) answered {(int)reply.StatusCode}");
        }
    }

    [Fact]
    public async Task Its_traces_and_metrics_carry_its_own_service_name()
    {
        await using var host = service.Start();

        var resource = host.Services.GetRequiredService<TracerProvider>().GetResource();
        Assert.Contains(resource.Attributes, a => a.Key == "service.name" && (string)a.Value == "crowdfunding-notifications");
    }
}
