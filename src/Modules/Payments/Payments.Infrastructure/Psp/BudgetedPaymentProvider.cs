using BuildingBlocks.Application;

using Payments.Application;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Every PSP call takes its tokens first, and waits for them: the PSP's limit, not the number of
/// tasks, sets the pace (lesson s3-a04). A 429 that arrives anyway empties the bucket it names, so
/// every task slows down, and the call is sent again when a token is free. A card setup has already
/// taken its turn in the card-setup lane, which answers a full lane with a wait instead of waiting.
/// </summary>
internal sealed class BudgetedPaymentProvider(IPaymentProvider psp, ITokenBuckets buckets, PspBudget budget, TimeProvider clock) : IPaymentProvider
{
    public Task<string> CreateCustomer(Guid backerId, CancellationToken ct) =>
        Call(PspCall.Customer, c => psp.CreateCustomer(backerId, c), ct);

    public Task<CardSetupIntent> StartCardSetup(string pspCustomerId, CancellationToken ct) =>
        psp.StartCardSetup(pspCustomerId, ct);

    public Task<ChargeOutcome> Charge(ChargeRequest request, string idempotencyKey, CancellationToken ct) =>
        Call(PspCall.Charge, c => psp.Charge(request, idempotencyKey, c), ct);

    public Task<ChargeOutcome> LookUp(ChargeReference reference, CancellationToken ct) =>
        Call(PspCall.LookUp, c => psp.LookUp(reference, c), ct);

    public Task<OnSessionPayment> StartOnSessionPayment(ChargeRequest request, string idempotencyKey, CancellationToken ct) =>
        Call(PspCall.Charge, c => psp.StartOnSessionPayment(request, idempotencyKey, c), ct);

    public Task<ChargeOutcome> Cancel(ChargeReference reference, CancellationToken ct) =>
        Call(PspCall.Charge, c => psp.Cancel(reference, c), ct);

    // Refunds and transfers are writes, so they share the charges' bucket.
    public Task<string> Refund(string pspPaymentId, long amountMinor, string idempotencyKey, CancellationToken ct) =>
        Call(PspCall.Charge, c => psp.Refund(pspPaymentId, amountMinor, idempotencyKey, c), ct);

    public Task<string> Transfer(Guid creatorId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct) =>
        Call(PspCall.Charge, c => psp.Transfer(creatorId, amountMinor, currency, idempotencyKey, c), ct);

    // The report is a read, so it shares the look-ups' bucket.
    public Task<BalanceReport> BalanceTransactions(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Call(PspCall.LookUp, c => psp.BalanceTransactions(from, to, c), ct);

    private async Task<T> Call<T>(PspCall call, Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        while (true)
        {
            await TakeTurn(call, ct);
            try
            {
                return await send(ct);
            }
            catch (PspRateLimitedException limited)
            {
                await buckets.Drain(limited.Global ? budget.Account : budget.Endpoint(call), ct);
            }
        }
    }

    private async Task TakeTurn(PspCall call, CancellationToken ct)
    {
        while (true)
        {
            var turn = await buckets.TryTake(budget.For(call), ct);
            if (turn.Granted)
            {
                return;
            }

            await Task.Delay(turn.RetryAfter, clock, ct);
        }
    }
}
