using BuildingBlocks.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuildingBlocks.Infrastructure.Events;

public static class EventRegistration
{
    /// <summary>The bus and the dispatcher, once per program.</summary>
    public static IServiceCollection AddInProcessEvents(this IServiceCollection services)
    {
        services.TryAddSingleton<EventBus>();
        services.TryAddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        services.AddOptions<OutboxOptions>();
        return services;
    }

    /// <summary>
    /// A module's outbox and inbox, on its own database context. Its outbox is the keyed
    /// <see cref="IOutbox"/> named after the module.
    /// </summary>
    public static IServiceCollection AddModuleEvents<TContext>(this IServiceCollection services, string module)
        where TContext : DbContext
    {
        services.TryAddScoped<PendingEvents>();
        services.TryAddScoped<OutboxCommitInterceptor>();
        services.AddKeyedScoped<IOutbox>(module, (sp, _) =>
            new EfOutbox<TContext>(sp.GetRequiredService<TContext>(), sp.GetRequiredService<PendingEvents>(), module));
        services.AddSingleton<IModuleEvents>(sp => new ModuleEvents<TContext>(module, sp.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }

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
