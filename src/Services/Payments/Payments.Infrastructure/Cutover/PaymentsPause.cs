using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Valkey;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace Payments.Infrastructure.Cutover;

/// <summary>
/// The pause flag of a data move (lesson s4-a06): while it is set, Payments' endpoints answer 503
/// with Retry-After and its jobs take no work. It gives clean answers; it is not the fence. A task
/// that cannot read it carries on, and the fence at the database stops its writes.
/// </summary>
internal interface IPaymentsPause
{
    /// <summary>Whether every task sees the same flag: false for the in-memory one of a laptop.</summary>
    bool IsShared { get; }

    Task<bool> IsSet(CancellationToken cancellationToken);

    Task Set(CancellationToken cancellationToken);

    Task Clear(CancellationToken cancellationToken);
}

/// <summary>One key in the shared Valkey, read by every Payments task.</summary>
internal sealed partial class ValkeyPaymentsPause(IConnectionMultiplexer valkey, TimeProvider clock, ILogger<ValkeyPaymentsPause> logger) : IPaymentsPause
{
    public static readonly RedisKey Key = "payments:pause";

    public bool IsShared => true;

    public async Task<bool> IsSet(CancellationToken cancellationToken)
    {
        try
        {
            return await valkey.GetDatabase().KeyExistsAsync(Key);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogUnreadable(logger, e);
            return false;
        }
    }

    // The command must know the flag is set before it fences, so a failure here is the command's failure.
    public Task Set(CancellationToken cancellationToken) =>
        valkey.GetDatabase().StringSetAsync(Key, clock.GetUtcNow().ToString("O"));

    public Task Clear(CancellationToken cancellationToken) => valkey.GetDatabase().KeyDeleteAsync(Key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey could not be reached for the pause flag; the database fence decides")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);
}

/// <summary>No Valkey: a flag in this process only, for a laptop that runs one.</summary>
internal sealed class InProcessPaymentsPause : IPaymentsPause
{
    private volatile bool _set;

    public bool IsShared => false;

    public Task<bool> IsSet(CancellationToken cancellationToken) => Task.FromResult(_set);

    public Task Set(CancellationToken cancellationToken)
    {
        _set = true;
        return Task.CompletedTask;
    }

    public Task Clear(CancellationToken cancellationToken)
    {
        _set = false;
        return Task.CompletedTask;
    }
}

/// <summary>Every Payments endpoint answers 503 with Retry-After while the flag is set.</summary>
internal sealed class PausedPayments(IPaymentsPause pause) : IEndpointFilter
{
    public const int RetryAfterSeconds = 30;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await pause.IsSet(context.HttpContext.RequestAborted))
        {
            return await next(context);
        }

        context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            // No duration: the move's pause is estimated at tens of minutes, and only a rehearsal times it (CQA-2113).
            title: "Payments is paused while its data moves. Send the request again after Retry-After; a change keeps its Idempotency-Key.",
            extensions: new Dictionary<string, object?> { ["reason"] = "payments_paused" });
    }
}

/// <summary>While the flag is set, Payments' events neither leave through the broker nor are read from its queue.</summary>
internal sealed class PaymentsEventPause(IPaymentsPause pause) : IEventFlowPause
{
    public async Task<bool> IsPaused(string module, CancellationToken cancellationToken) =>
        module == Contracts.PaymentsModuleName.Value && await pause.IsSet(cancellationToken);
}

internal static class PaymentsPauseRegistration
{
    public static IServiceCollection AddPaymentsPause(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.HasValkey())
        {
            services.TryAddSingleton<IPaymentsPause, ValkeyPaymentsPause>();
        }
        else
        {
            services.TryAddSingleton<IPaymentsPause, InProcessPaymentsPause>();
        }

        services.AddSingleton<IEventFlowPause, PaymentsEventPause>();
        return services;
    }
}
