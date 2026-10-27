using System.Security.Claims;

using Amazon.CognitoIdentityProvider;
using Amazon.SimpleEmailV2;

using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Payments.Contracts;

using Pledges.Contracts;

namespace Notifications;

public static class NotificationsModule
{
    public const string Name = "Notifications";

    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(Name)
            ?? throw new InvalidOperationException("ConnectionStrings:Notifications is not set.");
        services.AddDbContext<NotificationsDbContext>((sp, db) => NotificationsDbOptions
            .Configure((DbContextOptionsBuilder<NotificationsDbContext>)db, connectionString)
            .AddInterceptors(sp.GetRequiredService<OutboxCommitInterceptor<NotificationsDbContext>>()));
        services.AddModuleEvents<NotificationsDbContext>(Name);
        services.AddEventHandler<CampaignPublished, Handlers>(Name);
        services.AddEventHandler<CampaignSettled, Handlers>(Name);
        services.AddEventHandler<PledgeActivated, Handlers>(Name);
        services.AddEventHandler<PledgeCancelled, Handlers>(Name);
        services.AddEventHandler<CampaignFundingDecided, Handlers>(Name);
        services.AddEventHandler<CampaignCancelled, Handlers>(Name);
        services.AddEventHandler<PledgeReleased, Handlers>(Name);
        services.AddEventHandler<PledgeChargeSucceeded, Handlers>(Name);
        services.AddEventHandler<PledgeChargeFailed, Handlers>(Name);
        services.AddSingleton<IModuleMigrator, NotificationsMigrator>();
        services.AddSingleton(new ModuleDatabase(Name, NotificationsDbContext.Schema, connectionString));
        services.AddScoped<Notifier>();

        var options = configuration.GetSection(Name);
        services.AddOptions<NotificationsOptions>().Bind(options)
            .Validate(o => o.Sender is "InMemory" or "Ses", "Notifications:Sender must be InMemory or Ses.")
            .Validate(o => o.Sender != "Ses" || (o.FromAddress.Contains('@', StringComparison.Ordinal) && o.UserPoolId.Length > 0),
                "Notifications: sending through SES needs FromAddress and UserPoolId.")
            .Validate(o => o.SendsPerSecond > 0 && o.MaxConcurrentSends > 0 && o.BatchSize > 0 && o.MaxAttempts > 0
                    && o.PollEvery > TimeSpan.Zero && o.RetryAfter > TimeSpan.Zero && o.AddressFreshFor > TimeSpan.Zero
                    && o.Lease > TimeSpan.FromSeconds(2 * o.BatchSize / o.SendsPerSecond),
                "Notifications: the send rate, sizes and waits must be positive, and a batch sent at SendsPerSecond must take less than half the Lease.")
            .ValidateOnStart();
        if (options["Sender"] == "Ses")
        {
            services.AddSingleton<IAmazonSimpleEmailServiceV2, AmazonSimpleEmailServiceV2Client>();
            services.AddSingleton<IAmazonCognitoIdentityProvider, AmazonCognitoIdentityProviderClient>();
            services.AddSingleton<IEmailSender, SesEmailSender>();
            services.AddSingleton<IUserDirectory, CognitoUserDirectory>();
        }
        else
        {
            services.AddSingleton<InMemoryEmailSender>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<InMemoryEmailSender>());
            services.AddSingleton<IUserDirectory, InMemoryUserDirectory>();
        }

        services.AddScoped<EmailQueue>();
        services.AddScoped<AddressBook>();
        services.AddSingleton<EmailWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<EmailWorker>());
        services.AddSingleton<IStopParticipant>(sp => sp.GetRequiredService<EmailWorker>());

        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        // Anyone signed in may turn a kind of email off, or back on.
        app.MapPut("/v1/me/notifications/{kind}", async (string kind, PreferenceRequest request, ClaimsPrincipal user, NotificationsDbContext db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notifications.notification_preferences (user_id, kind, enabled) VALUES ({user.UserId()}, {kind}, {request.Enabled})
                ON CONFLICT (user_id, kind) DO UPDATE SET enabled = EXCLUDED.enabled
                """, ct);
            return Results.NoContent();
        }).RequireAuthorization().WithTags(Name);
        return app;
    }
}

internal sealed record PreferenceRequest(bool Enabled);

/// <summary>Brings the Notifications schema up to date. Run by the host's <c>migrate</c> command.</summary>
internal sealed class NotificationsMigrator : IModuleMigrator
{
    public string Module => NotificationsModule.Name;

    public string Schema => NotificationsDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        var options = NotificationsDbOptions.Configure(new DbContextOptionsBuilder<NotificationsDbContext>(), ownerConnectionString).Options;
        await using var db = new NotificationsDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
