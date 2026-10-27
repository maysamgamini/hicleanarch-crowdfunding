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

    private readonly ConcurrentDictionary<string, string> _setups = new();
    private readonly ConcurrentDictionary<string, ChargeOutcome> _byKey = new();
    private readonly ConcurrentDictionary<string, (ChargeRequest Request, ChargeOutcome Outcome)> _payments = new();
    private readonly ConcurrentQueue<(string Call, DateTimeOffset At)> _calls = new();
    private int _attempts;
    private int _loseNextReplies;
    private int _unreachable;
    private int _rateLimitNext;
    private string? _rateLimitCall;

    /// <summary>How many charge calls the PSP has actually carried out (a stored replay is not one).</summary>
    public int ChargesMade => _payments.Count;

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

    /// <summary>Each call that reached the PSP, by kind and time: a refused call counts, one that never left us does not.</summary>
    public IReadOnlyList<(string Call, DateTimeOffset At)> Calls => [.. _calls];

    /// <summary>Every call that was made, reachable or not: what an open circuit saves.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public Task<string> CreateCustomer(Guid backerId, CancellationToken ct)
    {
        Reach("customer");
        return Task.FromResult($"cus_{backerId:N}");
    }

    public Task<CardSetupIntent> StartCardSetup(string pspCustomerId, CancellationToken ct)
    {
        Reach("card-setup");
        var id = $"seti_{Guid.NewGuid():N}";
        _setups[id] = pspCustomerId;
        return Task.FromResult(new CardSetupIntent(id, $"{id}_secret_{Guid.NewGuid():N}"));
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
                CardNeedsAuthentication => new ChargeOutcome.Declined("authentication_required", paymentId),
                _ => new ChargeOutcome.Succeeded(paymentId),
            };
            _payments[paymentId] = (request, result);
            return result;
        });

        if (Interlocked.Decrement(ref _loseNextReplies) >= 0)
        {
            return Task.FromResult<ChargeOutcome>(new ChargeOutcome.Unknown("timeout"));
        }

        Interlocked.Exchange(ref _loseNextReplies, 0);
        return Task.FromResult(outcome);
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

    /// <summary>The backer finished the PSP's card form: the webhook the PSP would send, signed.</summary>
    public SignedWebhook CompleteSetup(string pspSetupId, string paymentMethodRef = CardSucceeds) =>
        Webhook("setup_succeeded", new { setup_id = pspSetupId, payment_method = paymentMethodRef });

    public SignedWebhook Webhook(string type, object data)
    {
        var body = JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type, data });
        return new SignedWebhook(body, WebhookSignature.Sign(options.Value.WebhookSecret, body, clock.GetUtcNow()));
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
    }
}

public sealed record SignedWebhook(string Body, string Signature);
