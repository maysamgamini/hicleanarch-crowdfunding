using System.Collections.Concurrent;
using System.Threading.RateLimiting;

using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Notifications;

/// <summary>
/// On every task, sends the emails the handlers recorded: claims a batch, sends it with up to
/// <see cref="NotificationsOptions.MaxConcurrentSends"/> in flight, paced to
/// <see cref="NotificationsOptions.SendsPerSecond"/>, and marks each recipient on their own. One
/// recipient's failure is retried later, with a growing wait, and holds back nobody else. When the
/// task stops, sends in flight finish and the rest of the batch is given back at once.
/// </summary>
internal sealed partial class EmailWorker(
    IServiceScopeFactory scopes,
    IEmailSender sender,
    IOptions<NotificationsOptions> options,
    ILogger<EmailWorker> logger) : BackgroundService, IStopParticipant
{
    private static readonly TimeSpan LongestRetry = TimeSpan.FromHours(1);

    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly TokenBucketRateLimiter _pace = Pace(options.Value);

    public string Name => "email worker";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _stopping.Token);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var claimed = 0;
                await _pass.WaitAsync(stop.Token);
                try
                {
                    claimed = await Pass(stop.Token);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogPassFailed(logger, e);
                }
                finally
                {
                    _pass.Release();
                }

                if (claimed < options.Value.BatchSize)
                {
                    await Task.Delay(options.Value.PollEvery, stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>Stops starting sends, and waits for the pass in progress to finish its sends and give back the rest.</summary>
    public async Task Stop(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await _pass.WaitAsync(cancellationToken);
        _pass.Release();
    }

    public override void Dispose()
    {
        _pace.Dispose();
        _stopping.Dispose();
        _pass.Dispose();
        base.Dispose();
    }

    private async Task<int> Pass(CancellationToken stop)
    {
        IReadOnlyList<ClaimedEmail> batch;
        await using (var scope = scopes.CreateAsyncScope())
        {
            batch = await scope.ServiceProvider.GetRequiredService<EmailQueue>().Claim(options.Value.BatchSize, options.Value.Lease, stop);
        }

        var notStarted = new ConcurrentDictionary<ClaimedEmail, bool>(batch.Select(e => KeyValuePair.Create(e, true)));
        try
        {
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxConcurrentSends, CancellationToken = stop }, async (email, ct) =>
            {
                using var turn = await _pace.AcquireAsync(1, ct);
                notStarted.TryRemove(email, out _);
                await SendOne(email); // once started, a send and its mark finish even if the task is stopping
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !notStarted.IsEmpty)
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<EmailQueue>().GiveBack(notStarted.Keys.ToList(), CancellationToken.None);
            throw;
        }

        return batch.Count;
    }

    private async Task SendOne(ClaimedEmail email)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<EmailQueue>();
        try
        {
            try
            {
                var address = await scope.ServiceProvider.GetRequiredService<AddressBook>().EmailOf(email.RecipientId, CancellationToken.None);
                if (address is null)
                {
                    await queue.NoAddress(email, CancellationToken.None);
                    return;
                }

                await sender.Send(new Email(email.RecipientId, address, email.Subject, email.Body), CancellationToken.None);
            }
            catch (Exception e)
            {
                var giveUp = email.Attempts >= options.Value.MaxAttempts;
                await queue.Failed(email, RetryAfter(email.Attempts), giveUp, e.Message, CancellationToken.None);
                if (giveUp)
                {
                    LogGaveUp(logger, e, email.Kind, email.RecipientId, email.EventId, email.Attempts);
                }

                return;
            }

            await queue.Sent(email, CancellationToken.None);
        }
        catch (Exception e)
        {
            // The mark did not reach the database: the lease runs out and the email is claimed again.
            LogMarkFailed(logger, e, email.EventId, email.RecipientId);
        }
    }

    /// <summary>The wait after the given failed attempt: the first wait, doubled each time, up to an hour.</summary>
    private TimeSpan RetryAfter(int attempts)
    {
        var wait = options.Value.RetryAfter * Math.Pow(2, Math.Min(attempts - 1, 20));
        return wait < LongestRetry ? wait : LongestRetry;
    }

    /// <summary>A bucket refilled ten times a second, so sends are spread over the second rather than sent in one burst.</summary>
    private static TokenBucketRateLimiter Pace(NotificationsOptions options)
    {
        var perPeriod = Math.Max(1, (int)Math.Ceiling(options.SendsPerSecond / 10));
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = perPeriod,
            TokensPerPeriod = perPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(perPeriod / options.SendsPerSecond),
            QueueLimit = options.MaxConcurrentSends,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The email worker's pass failed; the next pass tries again")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gave up the {Kind} email to {RecipientId} for event {EventId} after {Attempts} attempts")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, string kind, Guid recipientId, Guid eventId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the outcome of the email for event {EventId} to {RecipientId}; it is claimed again after its lease")]
    private static partial void LogMarkFailed(ILogger logger, Exception exception, Guid eventId, Guid recipientId);
}
