using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CrowdFunding.Messaging;

public static class BrokerRegistration
{
    /// <summary>
    /// Events through SNS and SQS, configuration section <c>Events:Broker</c>: the publisher behind the
    /// outbox, the reader of this host's queues, and the subscription check. Call after
    /// <see cref="EventRegistration.AddInProcessEvents"/>; with no such section, the program keeps every
    /// event in process.
    /// </summary>
    public static IServiceCollection AddBrokerEvents(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Events:Broker").Get<BrokerOptions>() ?? new BrokerOptions();
        if (options.MaxCallsInFlight <= 0 || options.MaxConcurrentMessages <= 0 || options.WaitSeconds is < 0 or > 20
            || options.VisibilityTimeoutSeconds is < 3 or > 43_200)
        {
            throw new InvalidOperationException(
                "Events:Broker: MaxCallsInFlight and MaxConcurrentMessages must be positive, WaitSeconds 0 to 20, VisibilityTimeoutSeconds 3 to 43200.");
        }

        // Topics are named by module and queues by service; configuration keys ignore case, so these do too.
        options.Topics = new(options.Topics, StringComparer.OrdinalIgnoreCase);
        options.Queues = new(options.Queues, StringComparer.OrdinalIgnoreCase);
        options.AlsoServe = new(options.AlsoServe, StringComparer.OrdinalIgnoreCase);
        services.AddSingleton(options);
        // On AWS the clients take the task role's credentials; an emulator (tests) takes any.
        var emulator = string.IsNullOrEmpty(options.ServiceUrl) ? null : new BasicAWSCredentials("test", "test");
        services.AddSingleton<IAmazonSimpleNotificationService>(_ => emulator is null
            ? new AmazonSimpleNotificationServiceClient(Configure(new AmazonSimpleNotificationServiceConfig(), options))
            : new AmazonSimpleNotificationServiceClient(emulator, Configure(new AmazonSimpleNotificationServiceConfig(), options)));
        services.AddSingleton<IAmazonSQS>(_ => emulator is null
            ? new AmazonSQSClient(Configure(new AmazonSQSConfig(), options))
            : new AmazonSQSClient(emulator, Configure(new AmazonSQSConfig(), options)));
        services.AddSingleton(sp => BrokerPlan.Load(
            Path.Combine(AppContext.BaseDirectory, sp.GetRequiredService<IOptions<EventRoutingOptions>>().Value.SubscriptionsFile)));

        // The check starts first: a route without its subscription stops the start before anything is published or read.
        services.AddHostedService<SubscriptionCheck>();
        services.AddSingleton<IEventPublisher, SnsEventPublisher>();
        services.AddBrokerPublishing();
        if (options.Read.Count > 0)
        {
            services.AddSingleton<SqsEventReader>();
            services.AddHostedService(sp => sp.GetRequiredService<SqsEventReader>());
            services.AddSingleton<IStopParticipant>(sp => sp.GetRequiredService<SqsEventReader>());
        }

        return services;
    }

    /// <summary>
    /// The SDK's standard retry mode: throttling and transient errors are retried with exponential,
    /// jittered back-off, three attempts in all.
    /// </summary>
    private static T Configure<T>(T config, BrokerOptions options)
        where T : ClientConfig
    {
        config.RetryMode = RequestRetryMode.Standard;
        config.MaxErrorRetry = 2;
        if (string.IsNullOrEmpty(options.ServiceUrl))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
        }

        return config;
    }
}
