using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using BuildingBlocks.Application;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

using Payments.Application;
using Payments.Infrastructure.Psp;

using Pledges.Application;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l13's journey view: each service logs one line per step it takes, with the campaign and the
/// pledge, to its own log group; the journey script lists a campaign's steps from all four services and
/// marks a service that logged none.
/// </summary>
public sealed class JourneyTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Services = ["campaigns", "pledges", "payments", "notifications"];

    [Fact]
    public async Task A_campaign_s_steps_come_from_all_four_services_and_a_missing_service_shows_as_a_gap()
    {
        // Each service's console, as its log group receives it (the hosts' JSON console formatter).
        var lines = new ConcurrentQueue<(string Service, string Json)>();
        await using var host = database.StartHost(
            services: s => s.AddSingleton<ILoggerProvider>(sp => new ConsoleCopy("pledges", lines).Using(sp)),
            payments: web => web.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(sp => new ConsoleCopy("payments", lines).Using(sp))),
            campaigns: web => web.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(sp => new ConsoleCopy("campaigns", lines).Using(sp))),
            notifications: s => s.AddSingleton<ILoggerProvider>(sp => new ConsoleCopy("notifications", lines).Using(sp)));
        var given = new Scenario(database, host);
        var campaign = await given.Publish(1_000);
        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = 6_000, tier_id = (string?)null, payment_method_id = FakePaymentProvider.CardSucceeds }, Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 second' WHERE campaign_id = @id", campaign.CampaignId);
        await Send<CloseDueCampaigns, int>(host, new CloseDueCampaigns(campaign.CampaignId));
        await Scenario.Eventually(async () =>
            await given.Scalar<long>("SELECT count(*) FROM payments.charges WHERE campaign_id = @id", campaign.CampaignId) == 1
            && await given.Scalar<long>("SELECT count(*) FROM payments.settlement_jobs WHERE campaign_id = @id", campaign.CampaignId) == 1);
        Assert.True(await Send<RunSettlement, bool>(host, new RunSettlement("journey", campaign.CampaignId)));

        // The campaign's last steps: Campaigns takes CampaignSettled, and Notifications has taken its events.
        await Scenario.Eventually(() => Task.FromResult(
            Steps(lines, campaign.CampaignId).Any(s => s.Step == "Campaigns.FollowSettlement" && s.Outcome == "handled")
            && Steps(lines, campaign.CampaignId).Any(s => s.Service == "notifications")));
        var steps = Steps(lines, campaign.CampaignId);

        Assert.Contains(steps, s => s.Service == "campaigns" && s.Step == "CampaignPublished" && s.Module == "Campaigns" && s.Outcome == "committed");
        Assert.Contains(steps, s => s.Service == "pledges" && s.Step == "Pledges.OpenFundingWindow" && s.Outcome == "handled");
        Assert.Contains(steps, s => s.Service == "payments" && s.Step == "Payments.CreateCharge" && s.PledgeId is not null && s.Outcome == "handled");
        Assert.Contains(steps, s => s.Service == "notifications" && s.Module == "Notifications" && s.Outcome == "handled");
        // Each service logs only what it ran: a handler's line comes from the host of its module.
        Assert.All(steps.Where(s => s.Outcome != "committed"), s => Assert.Equal(s.Service, s.Module.ToLowerInvariant()));
        // A handler's step carries the trace it ran in (a child of the change that wrote the event, or a linked one).
        Assert.All(steps.Where(s => s.Outcome == "handled"), s => Assert.Matches("^[0-9a-f]{32}$", s.TraceId));

        // The journey script over what each service wrote to its own log group.
        var all = await Journey(campaign.CampaignId, steps);
        Assert.All(Services, s => Assert.Contains($"\t{s}\t", all, StringComparison.Ordinal));
        Assert.DoesNotContain("GAP", all, StringComparison.Ordinal);

        var withoutNotifications = await Journey(campaign.CampaignId, [.. steps.Where(s => s.Service != "notifications")]);
        Assert.Contains("GAP\tnotifications\tno step logged for this campaign", withoutNotifications, StringComparison.Ordinal);
        Assert.Single(withoutNotifications.Split('\n'), l => l.StartsWith("GAP", StringComparison.Ordinal));
    }

    private sealed record JourneyStep(string Service, string Step, string Module, string? PledgeId, string Outcome, string TraceId);

    /// <summary>
    /// One campaign's journey lines, read as Logs Insights reads the JSON console: the fields under
    /// <c>State</c>, the step's line by its event id.
    /// </summary>
    private static List<JourneyStep> Steps(ConcurrentQueue<(string Service, string Json)> lines, Guid campaignId) =>
        [.. lines
            .Select(l => (l.Service, Root: JsonDocument.Parse(l.Json).RootElement))
            .Where(l => l.Root.GetProperty("EventId").GetInt32() == 7000)
            .Select(l => (l.Service, State: l.Root.GetProperty("State")))
            .Where(l => l.State.TryGetProperty("campaign.id", out var c) && c.GetString() == campaignId.ToString())
            .Select(l => new JourneyStep(l.Service, l.State.GetProperty("journey.step").GetString()!, l.State.GetProperty("module").GetString()!,
                l.State.TryGetProperty("pledge.id", out var p) ? p.GetString() : null, l.State.GetProperty("outcome").GetString()!,
                l.State.TryGetProperty("trace.id", out var t) ? t.GetString() ?? "" : ""))];

    /// <summary>Runs scripts/journey.sh on a Logs Insights reply made of the steps, one log group per service.</summary>
    private static async Task<string> Journey(Guid campaignId, IReadOnlyList<JourneyStep> steps)
    {
        var at = DateTimeOffset.UtcNow;
        var rows = new JsonArray([.. steps.Select((s, i) => (JsonNode)new JsonArray(
            Field("@timestamp", at.AddMilliseconds(i).ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)),
            Field("@log", $"123456789012:/hicleanarch/sandbox/{s.Service}"),
            Field("step", s.Step),
            Field("pledge", s.PledgeId ?? ""),
            Field("outcome", s.Outcome)))]);
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, new JsonObject { ["status"] = "Complete", ["results"] = rows }.ToJsonString(), Ct);
            using var script = Process.Start(new ProcessStartInfo("bash", [Path.Combine(Root(), "scripts", "journey.sh"), campaignId.ToString(),
                "--services", string.Join(',', Services), "--results", file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = await script.StandardOutput.ReadToEndAsync(Ct);
            await script.WaitForExitAsync(Ct);
            Assert.True(script.ExitCode == 0, await script.StandardError.ReadToEndAsync(Ct));
            return output;
        }
        finally
        {
            File.Delete(file);
        }

        static JsonObject Field(string name, string value) => new() { ["field"] = name, ["value"] = value };
    }

    private static async Task<T> Send<TCommand, T>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, TCommand command)
        where TCommand : ICommand<T>
    {
        await using var scope = host.ServicesFor<TCommand>().CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<TCommand, T>(command, Ct)).Value;
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CrowdFunding.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>Writes one service's records as its console does, with the formatter the host chose.</summary>
    private sealed class ConsoleCopy(string service, ConcurrentQueue<(string Service, string Json)> lines) : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;
        private ConsoleFormatter? _formatter;

        private void Add(string json) => lines.Enqueue((service, json));

        public ConsoleCopy Using(IServiceProvider services)
        {
            var name = services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName ?? ConsoleFormatterNames.Simple;
            _formatter = services.GetServices<ConsoleFormatter>().Single(f => f.Name == name);
            return this;
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Copy(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Copy(ConsoleCopy console, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 7000)
                {
                    return; // only the journey lines: the rest of the console is not this test's
                }

                using var writer = new StringWriter();
                console._formatter!.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter), console._scopes, writer);
                console.Add(writer.ToString());
            }
        }
    }
}
