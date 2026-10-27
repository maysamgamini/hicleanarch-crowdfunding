using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Payments.Application;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// The course's PSP: in memory, deterministic, and faithful where the design depends on it. It
/// keeps the first result for an idempotency key and returns it to every retry, saves cards in a
/// separate step that ends with a signed webhook, chooses a charge's outcome from the card like a
/// PSP's test cards, and can be told to lose its reply after charging, so a caller sees a timeout.
/// Its memory lasts as long as the process.
/// </summary>
public sealed class FakePaymentProvider(IOptions<PaymentsOptions> options, TimeProvider clock) : IPaymentProvider
{
    /// <summary>Cards that behave like a PSP's test cards.</summary>
    public const string CardSucceeds = "pm_card_visa";
    public const string CardDeclined = "pm_card_declined";
    public const string CardNeedsAuthentication = "pm_card_authentication_required";
    public const string CardExpired = "pm_card_expired";

    /// <summary>Declined as fraudulent, with the advice never to try it again.</summary>
    public const string CardFraudulent = "pm_card_fraudulent";

    /// <summary>A generic decline whose advice is to try again later.</summary>
    public const string CardTryAgainLater = "pm_card_try_again_later";

    private readonly ConcurrentDictionary<string, string> _customersByKey = new();
    private readonly ConcurrentDictionary<string, (string Customer, CardSetupIntent Intent)> _setupsByKey = new();
    private readonly ConcurrentDictionary<string, ChargeOutcome> _byKey = new();
    private readonly ConcurrentDictionary<string, (ChargeRequest Request, ChargeOutcome Outcome)> _payments = new();
    private readonly ConcurrentDictionary<string, ChargeOutcome> _onSession = new();
    private readonly ConcurrentQueue<(string Call, DateTimeOffset At)> _calls = new();
    private int _attempts;
    private readonly ConcurrentDictionary<string, string> _refundsByKey = new();
    private readonly ConcurrentDictionary<string, string> _transfersByKey = new();
    private readonly ConcurrentDictionary<string, BalanceTransaction> _report = new();
    private long _reportLagTicks;
    private int _loseNextReplies;
    private int _unreachable;
    private int _rateLimitNext;
    private int _concurrencyLimitNext;
    private string? _rateLimitCall;
    private Hold? _cardSetupHold;

    /// <summary>How many charge calls the PSP has actually carried out (a stored replay is not one).</summary>
    public int ChargesMade => _payments.Count;

    /// <summary>How many customers the PSP has created (a stored replay is not one).</summary>
    public int CustomersMade => _customersByKey.Count;

    /// <summary>How many card setups the PSP has started (a stored replay is not one).</summary>
    public int CardSetupsMade => _setupsByKey.Count;

    /// <summary>The next <paramref name="count"/> charges are carried out, but their replies never arrive.</summary>
    public void LoseNextReplies(int count) => Interlocked.Exchange(ref _loseNextReplies, count);

    /// <summary>Calls fail before they reach the PSP, as if the network were down.</summary>
    public void Unreachable(bool unreachable) => Interlocked.Exchange(ref _unreachable, unreachable ? 1 : 0);

    /// <summary>The next <paramref name="count"/> calls of one kind ("charge", "lookup", ...) are refused for the endpoint's rate limit, as a PSP's 429.</summary>
    public void RateLimitNext(string call, int count)
    {
        Volatile.Write(ref _rateLimitCall, call);
        Interlocked.Exchange(ref _rateLimitNext, count);
    }

    /// <summary>
    /// Card setups that reach the PSP from now on wait until the hold is released, as a slow PSP does;
    /// <see cref="Hold.Reached"/> completes when the first one is waiting.
    /// </summary>
    public Hold HoldCardSetups()
    {
        var hold = new Hold();
        Volatile.Write(ref _cardSetupHold, hold);
        return hold;
    }

    /// <summary>The next <paramref name="count"/> calls of any kind are refused for too many requests in progress, as Stripe's 429 <c>global-concurrency</c>.</summary>
    public void LimitConcurrencyNext(int count) => Interlocked.Exchange(ref _concurrencyLimitNext, count);

    /// <summary>Each call that reached the PSP, by kind and time: a refused call counts, one that never left us does not.</summary>
    public IReadOnlyList<(string Call, DateTimeOffset At)> Calls => [.. _calls];

    /// <summary>Every call that was made, reachable or not: what an open circuit saves.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public Task<string> CreateCustomer(Guid backerId, string idempotencyKey, CancellationToken ct)
    {
        Reach("customer");
        return Task.FromResult(_customersByKey.GetOrAdd(idempotencyKey, _ => $"cus_{Guid.NewGuid():N}"));
    }

    public async Task<CardSetupIntent> StartCardSetup(string pspCustomerId, string idempotencyKey, CancellationToken ct)
    {
        Reach("card-setup");
        if (Volatile.Read(ref _cardSetupHold) is { } hold)
        {
            hold.Arrive();
            await hold.Released.WaitAsync(ct);
        }

        var (customer, intent) = _setupsByKey.GetOrAdd(idempotencyKey, _ =>
        {
            var id = $"seti_{Guid.NewGuid():N}";
            return (pspCustomerId, new CardSetupIntent(id, $"{id}_secret_{Guid.NewGuid():N}"));
        });

        // As at Stripe, a key sent again with other parameters is an error, not a new setup.
        return customer == pspCustomerId
            ? intent
            : throw new InvalidOperationException($"Idempotency key {idempotencyKey} was used with another customer.");
    }

    public Task<ChargeOutcome> Charge(ChargeRequest request, string idempotencyKey, CancellationToken ct)
    {
        Reach("charge");
        var outcome = _byKey.GetOrAdd(idempotencyKey, _ =>
        {
            var paymentId = $"pi_{Guid.NewGuid():N}";
            ChargeOutcome result = request.PaymentMethodRef switch
            {
                CardDeclined => new ChargeOutcome.Declined("insufficient_funds", paymentId),
                CardExpired => new ChargeOutcome.Declined("expired_card", paymentId),
                CardNeedsAuthentication => new ChargeOutcome.Declined("authentication_required", paymentId),
                CardFraudulent => new ChargeOutcome.Declined("fraudulent", paymentId, "do_not_try_again"),
                CardTryAgainLater => new ChargeOutcome.Declined("generic_decline", paymentId, "try_again_later"),
                _ => new ChargeOutcome.Succeeded(paymentId, FeeFor(request.AmountMinor)),
            };
            if (result is ChargeOutcome.Succeeded)
            {
                _report[paymentId] = new BalanceTransaction($"txn_{Guid.NewGuid():N}", paymentId, request.PledgeId,
                    request.AmountMinor, FeeFor(request.AmountMinor), request.Currency, clock.GetUtcNow());
            }

            _payments[paymentId] = (request, result);
            return result;
        });

        return Task.FromResult(TakeLostReply() ? new ChargeOutcome.Unknown("timeout") : outcome);
    }

    public Task<ChargeOutcome> LookUp(ChargeReference reference, CancellationToken ct)
    {
        Reach("lookup");
        var found = reference.PspPaymentId is { } id && _payments.TryGetValue(id, out var byId)
            ? byId.Outcome
            : _payments.Values
                .Where(p => p.Request.PspCustomerId == reference.PspCustomerId && p.Request.PledgeId == reference.PledgeId && p.Request.AttemptNo == reference.AttemptNo)
                .Select(p => p.Outcome)
                .FirstOrDefault();
        return Task.FromResult(found ?? new ChargeOutcome.NotFound());
    }

    public Task<OnSessionPayment> StartOnSessionPayment(ChargeRequest request, string idempotencyKey, CancellationToken ct)
    {
        Reach("charge");
        var outcome = _byKey.GetOrAdd(idempotencyKey, _ =>
        {
            var waiting = new ChargeOutcome.Unknown(ChargeOutcome.WaitingForBacker);
            var paymentId = $"pi_{Guid.NewGuid():N}";
            _payments[paymentId] = (request, waiting);
            _onSession[paymentId] = waiting;
            return new ChargeOutcome.Declined("requires_action", paymentId);
        });
        var id = ((ChargeOutcome.Declined)outcome).PspPaymentId!;
        return Task.FromResult(new OnSessionPayment(id, $"{id}_secret_{Guid.NewGuid():N}"));
    }

    public Task<ChargeOutcome> Cancel(ChargeReference reference, CancellationToken ct)
    {
        Reach("charge");
        var id = reference.PspPaymentId ?? _payments.Where(p =>
            p.Value.Request.PspCustomerId == reference.PspCustomerId && p.Value.Request.PledgeId == reference.PledgeId && p.Value.Request.AttemptNo == reference.AttemptNo)
            .Select(p => p.Key).FirstOrDefault();
        if (id is null || !_payments.TryGetValue(id, out var payment))
        {
            return Task.FromResult<ChargeOutcome>(new ChargeOutcome.NotFound());
        }

        if (payment.Outcome is ChargeOutcome.Unknown)
        {
            payment = (payment.Request, new ChargeOutcome.Declined("authentication_required", id));
            _payments[id] = payment;
        }

        return Task.FromResult(payment.Outcome);
    }

    /// <summary>
    /// The backer confirmed an on-session payment in the browser, or gave up: the PSP's webhook,
    /// signed. Once confirmed, a payment stays succeeded.
    /// </summary>
    public SignedWebhook FinishOnSessionPayment(string pspPaymentId, bool confirmed = true)
    {
        var (request, _) = _payments[pspPaymentId];
        ChargeOutcome result = confirmed ? new ChargeOutcome.Succeeded(pspPaymentId, FeeFor(request.AmountMinor)) : new ChargeOutcome.Declined("authentication_required", pspPaymentId);
        _payments[pspPaymentId] = (request, result);
        if (confirmed)
        {
            _report.TryAdd(pspPaymentId, new BalanceTransaction($"txn_{Guid.NewGuid():N}", pspPaymentId, request.PledgeId,
                request.AmountMinor, FeeFor(request.AmountMinor), request.Currency, clock.GetUtcNow()));
        }

        return Webhook(confirmed ? "payment_succeeded" : "payment_failed", new { payment_id = pspPaymentId, fee_minor = confirmed ? FeeFor(request.AmountMinor) : (long?)null, decline_code = confirmed ? null : "authentication_required" });
    }

    /// <summary>A common card list price: 2.9% plus 30 cents, rounded to the cent.</summary>
    public static long FeeFor(long amountMinor) => (long)Math.Round(amountMinor * 0.029m, MidpointRounding.AwayFromZero) + 30;

    /// <summary>How many refunds and transfers the PSP has actually made.</summary>
    public int RefundsMade => _refundsByKey.Count;

    public int TransfersMade => _transfersByKey.Count;

    public Task<string> Refund(string pspPaymentId, long amountMinor, string idempotencyKey, CancellationToken ct)
    {
        Reach("charge");
        return Task.FromResult(_refundsByKey.GetOrAdd(idempotencyKey, _ =>
        {
            var refundId = $"re_{Guid.NewGuid():N}";
            var (request, _) = _payments[pspPaymentId];
            _report[refundId] = new BalanceTransaction($"txn_{Guid.NewGuid():N}", refundId, request.PledgeId, -amountMinor, 0, request.Currency, clock.GetUtcNow());
            return refundId;
        }));
    }

    public Task<BalanceReport> BalanceTransactions(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        Reach("report");
        // Without a lag the fake's report is complete the moment money moves, through any end asked for.
        var lag = Interlocked.Read(ref _reportLagTicks);
        var covered = clock.GetUtcNow() - TimeSpan.FromTicks(lag);
        var until = lag == 0 || to < covered ? to : covered;
        return Task.FromResult(new BalanceReport([.. _report.Values.Where(t => t.CreatedAt >= from && t.CreatedAt <= until)], until));
    }

    /// <summary>The report is complete only up to <paramref name="lag"/> ago, as a real PSP's is a while behind.</summary>
    public void ReportLagsBy(TimeSpan lag) => Interlocked.Exchange(ref _reportLagTicks, lag.Ticks);

    /// <summary>The report shows a different fee for a payment than the charge returned, as a PSP's error or a fee change would.</summary>
    public void ReportFeeAs(string sourceId, long feeMinor) => _report[sourceId] = _report[sourceId] with { FeeMinor = feeMinor };

    /// <summary>The report has a movement our books never recorded.</summary>
    public void AddToReport(string sourceId, string pledgeId, long amountMinor, long feeMinor) =>
        _report[sourceId] = new BalanceTransaction($"txn_{Guid.NewGuid():N}", sourceId, pledgeId, amountMinor, feeMinor, "USD", clock.GetUtcNow());

    public Task<string> Transfer(Guid creatorId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct)
    {
        Reach("charge");
        return Task.FromResult(_transfersByKey.GetOrAdd(idempotencyKey, _ => $"tr_{Guid.NewGuid():N}"));
    }

    /// <summary>The backer finished the PSP's card form: the webhook the PSP would send, signed.</summary>
    public SignedWebhook CompleteSetup(string pspSetupId, string paymentMethodRef = CardSucceeds) =>
        Webhook("setup_succeeded", new { setup_id = pspSetupId, payment_method = paymentMethodRef });

    public SignedWebhook Webhook(string type, object data)
    {
        var body = JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type, data });
        return new SignedWebhook(body, WebhookSignature.Sign(options.Value.WebhookSecret, body, clock.GetUtcNow()));
    }

    /// <summary>
    /// One of the replies <see cref="LoseNextReplies"/> asked to lose, if any are left. It only ever
    /// counts down to zero, so concurrent charges never take more than were asked for, nor undo a new request.
    /// </summary>
    private bool TakeLostReply()
    {
        var left = Volatile.Read(ref _loseNextReplies);
        while (left > 0)
        {
            var seen = Interlocked.CompareExchange(ref _loseNextReplies, left - 1, left);
            if (seen == left)
            {
                return true;
            }

            left = seen;
        }

        return false;
    }

    private void Reach(string call)
    {
        Interlocked.Increment(ref _attempts);
        if (Volatile.Read(ref _unreachable) == 1)
        {
            throw new HttpRequestException("The fake PSP is unreachable.");
        }

        _calls.Enqueue((call, clock.GetUtcNow()));
        if (Volatile.Read(ref _rateLimitCall) == call && Interlocked.Decrement(ref _rateLimitNext) >= 0)
        {
            throw new PspRateLimitedException(global: false);
        }

        if (Volatile.Read(ref _concurrencyLimitNext) > 0 && Interlocked.Decrement(ref _concurrencyLimitNext) >= 0)
        {
            throw PspRateLimitedException.TooManyInFlight();
        }
    }
}

public sealed record SignedWebhook(string Body, string Signature);

/// <summary>Calls that wait at the fake PSP until the test lets them go.</summary>
public sealed class Hold
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a call is waiting.</summary>
    public Task Reached => _reached.Task;

    internal Task Released => _released.Task;

    public void Release() => _released.TrySetResult();

    internal void Arrive() => _reached.TrySetResult();
}
