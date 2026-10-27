extern alias CampaignsService;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using CampaignsHost = CampaignsService::Program;

namespace IntegrationTests;

/// <summary>
/// The Campaigns service started beside a test's monolith, on the same database and broker, and stopped with it
/// (lesson s5-l09: Campaigns runs nowhere else). It takes the monolith's settings that are Campaigns' to read:
/// every <c>Campaigns:*</c> key, its connection strings, the Valkey connection and the outbox's timings; so a test
/// that sets one of them on its host sets it where Campaigns runs.
/// </summary>
public sealed class CampaignsBeside(WebApplicationFactory<CampaignsHost> host) : IHostedService
{
    private static readonly string[] Passed = ["Campaigns:", "ConnectionStrings:Campaigns", "ConnectionStrings:Valkey", "Outbox:"];

    public WebApplicationFactory<CampaignsHost> Host => host;

    public IServiceProvider Services => host.Services;

    public static IReadOnlyDictionary<string, string?> SettingsFrom(IConfiguration monolith) =>
        monolith.AsEnumerable()
            .Where(kv => kv.Value is not null && Passed.Any(p => kv.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = host.Services; // starts it
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await host.DisposeAsync();
}

public static class CampaignsBesideExtensions
{
    /// <summary>The Campaigns service beside this monolith.</summary>
    public static CampaignsBeside Campaigns(this WebApplicationFactory<Program> monolith) =>
        monolith.Services.GetRequiredService<CampaignsBeside>();
}
