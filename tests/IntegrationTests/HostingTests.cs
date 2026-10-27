using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>What a task needs to come and go behind the load balancer (lesson s3-l01).</summary>
public sealed class HostingTests(Database database)
{
    [Fact]
    public async Task The_live_check_answers_even_when_a_database_is_unreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = database.StartHost(s => s.AddSingleton(
            new ModuleDatabase("Unreachable", "nowhere", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1")));
        var client = host.CreateClient();

        var live = await client.GetAsync("/health/live", ct);
        var ready = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>(ct);
        var failed = body.GetProperty("checks").EnumerateArray().Where(c => !c.GetProperty("ok").GetBoolean())
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["database:Unreachable"], failed);
    }

    [Fact]
    public async Task The_ready_check_tests_every_modules_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = database.StartHost();

        var ready = await host.CreateClient().GetFromJsonAsync<JsonElement>("/health/ready", ct);

        Assert.Equal("ready", ready.GetProperty("status").GetString());
        var names = ready.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("database:Campaigns", names);
        Assert.Contains("database:Pledges", names);
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
