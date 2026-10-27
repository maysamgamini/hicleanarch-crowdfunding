using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IntegrationTests;

/// <summary>What a task needs to come and go behind the load balancer (lesson s3-l01).</summary>
public sealed class HostingTests(Database database, Valkey valkey) : IClassFixture<Valkey>
{
    [Fact]
    public async Task The_live_check_answers_even_when_a_database_is_unreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = database.StartHost(s => s.AddSingleton(
            new ModuleDatabase("Unreachable", "nowhere", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1")));
        var client = host.CreateClient();

        var live = await client.GetAsync("/health/live", ct);
        var legacy = await client.GetAsync("/health", ct);
        var ready = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("not_ready", (await ready.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("status").GetString());
        Assert.Equal(["database:Unreachable"], Failed(await ReadyReport(host, ct)));
    }

    [Fact]
    public async Task The_public_ready_reply_says_only_whether_the_host_is_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = database.StartHost(s => s.AddSingleton(
            new ModuleDatabase("Unreachable", "nowhere", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1")));

        var reply = await host.CreateClient().GetAsync("/health/ready", ct);
        var body = await reply.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.StatusCode);
        Assert.Equal(["status"], JsonDocument.Parse(body).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unreachable", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_ready_check_tests_every_modules_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = database.StartHost();

        var ready = await host.CreateClient().GetFromJsonAsync<JsonElement>("/health/ready", ct);

        Assert.Equal("ready", ready.GetProperty("status").GetString());
        var report = await ReadyReport(host, ct);
        Assert.Subset(report.Entries.Keys.ToHashSet(), new HashSet<string>
        {
            "database:Campaigns", "database:Pledges", "database:PledgesJobs", "database:Payments", "database:Notifications",
        });
        Assert.Empty(Failed(report));
        Assert.DoesNotContain("valkey", report.Entries.Keys); // not configured, not checked
    }

    [Fact]
    public async Task The_ready_check_tests_valkey_when_it_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var up = database.StartHost().WithWebHostBuilder(web => web.UseSetting("ConnectionStrings:Valkey", valkey.Configuration));
        await using var down = database.StartHost().WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:Valkey", "127.0.0.1:1,abortConnect=false,connectTimeout=500"));

        var whenUp = await ReadyReport(up, ct);
        var whenDown = await down.CreateClient().GetAsync("/health/ready", ct);

        Assert.Equal(HealthStatus.Healthy, whenUp.Entries["valkey"].Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, whenDown.StatusCode);
        Assert.Equal(["valkey"], Failed(await ReadyReport(down, ct)));
    }

    /// <summary>The ready checks' own report: what the public reply leaves out, for the operator and these tests.</summary>
    private static Task<HealthReport> ReadyReport(WebApplicationFactory<Program> host, CancellationToken ct) =>
        host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Tags.Contains(HealthEndpoints.Ready), ct);

    private static List<string> Failed(HealthReport report) =>
        [.. report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy).Select(e => e.Key)];

    [Fact]
    public async Task A_request_that_needs_a_database_it_cannot_reach_is_a_503_with_retry_after()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Unreachable = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1";
        await using var host = database.StartHost().WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:Campaigns", Unreachable)
            .UseSetting("ConnectionStrings:CampaignsRead", Unreachable));

        var response = await host.CreateClient().GetAsync($"/v1/campaigns/cmp_{Guid.NewGuid():N}", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(DatabaseUnavailableHandler.RetryAfterSeconds), response.Headers.RetryAfter?.Delta);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("database_unavailable", problem.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("Payments:WebhookSecret", "too-short", "WebhookSecret")]
    [InlineData("Pledges:MarkBatchSize", "0", "MarkBatchSize")]
    [InlineData("Payments:Dunning:MaxAttempts", "0", "Dunning")]
    [InlineData("Outbox:MaxConcurrentDeliveries", "0", "MaxConcurrentDeliveries")]
    public void A_wrong_setting_stops_the_program_at_start_not_at_first_use(string key, string value, string named)
    {
        using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting(key, value));

        var refusal = Refusals.Of(host);

        Assert.Contains(named, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_gives_every_participant_its_turn_even_after_one_fails()
    {
        var failing = new Participant("failing", fail: true);
        var after = new Participant("after", fail: false);
        var host = database.StartHost(s => s
            .AddSingleton<IStopParticipant>(failing)
            .AddSingleton<IStopParticipant>(after));
        _ = host.Server; // start it

        await host.DisposeAsync();

        Assert.True(failing.Called);
        Assert.True(after.Called);
    }

    private sealed class Participant(string name, bool fail) : IStopParticipant
    {
        public bool Called { get; private set; }

        public string Name => name;

        public Task Stop(CancellationToken cancellationToken)
        {
            Called = true;
            return fail ? Task.FromException(new InvalidOperationException("cannot give back")) : Task.CompletedTask;
        }
    }
}
