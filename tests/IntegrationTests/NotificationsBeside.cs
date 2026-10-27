extern alias NotificationsService;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NotificationsHost = NotificationsService::Program;

namespace IntegrationTests;

/// <summary>
/// The Notifications service started beside a test's monolith, on the same database and broker, and
/// stopped with it (s5-l04): what the monolith's events reach. A test reads its services, or calls its endpoint.
/// </summary>
public sealed class NotificationsBeside(WebApplicationFactory<NotificationsHost> host) : IHostedService
{
    public WebApplicationFactory<NotificationsHost> Host => host;

    public IServiceProvider Services => host.Services;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = host.Services; // starts it
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await host.DisposeAsync();
}

public static class NotificationsBesideExtensions
{
    /// <summary>The Notifications service beside this monolith.</summary>
    public static NotificationsBeside Notifications(this WebApplicationFactory<Program> monolith) =>
        monolith.Services.GetRequiredService<NotificationsBeside>();
}
