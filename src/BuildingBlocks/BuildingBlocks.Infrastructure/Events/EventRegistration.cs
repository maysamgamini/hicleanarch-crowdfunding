using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuildingBlocks.Infrastructure.Events;

public static class EventRegistration
{
    /// <summary>The bus and the dispatcher, once per program; configuration section <c>Outbox</c>.</summary>
    public static IServiceCollection AddInProcessEvents(this IServiceCollection services)
    {
        // Routing first: a program whose events are not all routed stops here, before anything is delivered.
        services.AddOptions<EventRoutingOptions>().BindConfiguration("Events");
        services.TryAddSingleton(sp => EventSubscriptions.Load(
                Path.Combine(AppContext.BaseDirectory, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EventRoutingOptions>>().Value.SubscriptionsFile))
            .With(sp.GetServices<EventSubscription>()));
        services.TryAddSingleton<EventRoutes>();
        services.AddHostedService<EventRoutesCheck>();
        services.TryAddSingleton<EventBus>();
        services.TryAddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        services.AddSingleton<IStopParticipant>(sp => sp.GetRequiredService<OutboxDispatcher>());
        services.AddOptions<OutboxOptions>()
            .BindConfiguration("Outbox")
            .Validate(o => o.MaxConcurrentDeliveries > 0 && o.SweepBatch > 0, "Outbox: MaxConcurrentDeliveries and SweepBatch must be positive.")
            .Validate(o => o.SweepEvery > TimeSpan.Zero && o.RedeliverAfter > o.RetryDelays.Aggregate(TimeSpan.Zero, (a, d) => a + d),
                "Outbox: SweepEvery must be positive, and RedeliverAfter longer than every retry together.")
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// A module's outbox and inbox, on its own database context. Its outbox is the keyed
    /// <see cref="IOutbox"/> named after the module.
    /// </summary>
    public static IServiceCollection AddModuleEvents<TContext>(this IServiceCollection services, string module)
        where TContext : DbContext
    {
        services.TryAddScoped<PendingEvents<TContext>>();
        services.TryAddScoped<OutboxCommitInterceptor<TContext>>();
        services.AddKeyedScoped<IOutbox>(module, (sp, _) =>
            new EfOutbox<TContext>(sp.GetRequiredService<TContext>(), sp.GetRequiredService<PendingEvents<TContext>>(), sp.GetRequiredService<EventRoutes>(), module));
        services.AddSingleton<IModuleEvents>(sp => new ModuleEvents<TContext>(module, sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }

    /// <summary>
    /// A consumer the program adds to <c>events/subscriptions.json</c> for itself: for event types that exist
    /// only in tests. A real consumer belongs in the file, where every host and Terraform read it.
    /// </summary>
    public static IServiceCollection AddEventSubscription<TEvent>(this IServiceCollection services, string module)
        where TEvent : IntegrationEvent =>
        services.AddSingleton(new EventSubscription(typeof(TEvent).Name, module));

    /// <summary>Declares an event type the module publishes, so the dispatcher knows it.</summary>
    public static IServiceCollection AddPublishedEvent<TEvent>(this IServiceCollection services)
        where TEvent : IntegrationEvent =>
        services.AddSingleton(new PublishedEvent(typeof(TEvent)));

    /// <summary>A handler of <paramref name="module"/> for <typeparamref name="TEvent"/>; its inbox is that module's.</summary>
    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services, string module)
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new EventHandlerRegistration(typeof(TEvent), $"{module}.{typeof(THandler).Name}", module,
            (scope, e, ct) => scope.GetRequiredService<THandler>().Handle((TEvent)e, ct)));
        return services;
    }
}
