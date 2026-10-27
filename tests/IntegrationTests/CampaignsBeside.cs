extern alias CampaignsService;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using CampaignsHost = CampaignsService::Program;

namespace IntegrationTests;

/// <summary>Where Campaigns runs in a test's pair of hosts (lesson s5-l08).</summary>
public enum CampaignsAt
{
    /// <summary>In the monolith only (Modules:Campaigns:Mode = InProcess, no service beside it).</summary>
    Monolith,

    /// <summary>In the monolith and in the Campaigns service beside it, both reading the campaigns queue.</summary>
    Both,

    /// <summary>Only in the Campaigns service: the monolith runs with Modules:Campaigns:Mode = Remote.</summary>
    Service,
}

/// <summary>The Campaigns service started beside a test's monolith, on the same database and broker, and stopped with it.</summary>
public sealed class CampaignsBeside(WebApplicationFactory<CampaignsHost> host) : IHostedService
{
    public WebApplicationFactory<CampaignsHost> Host => host;

    public IServiceProvider Services => host.Services;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = host.Services; // starts it
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await host.DisposeAsync();
}

public static class CampaignsBesideExtensions
{
    /// <summary>The Campaigns service beside this monolith, if one was started.</summary>
    public static CampaignsBeside? Campaigns(this WebApplicationFactory<Program> monolith) =>
        monolith.Services.GetService<CampaignsBeside>();
}
