using BuildingBlocks.Application;

using Payments.Application;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Every PSP call takes its tokens first, then a slot of the account's calls in flight, and waits
/// for both: the PSP's limits, not the number of tasks, set the pace (lesson s3-a04). The slot is
/// given back when the call ends, whatever its end. A 429 that arrives anyway empties the bucket it
/// names, or, for too many calls in progress, shrinks the slots, so every task slows down, and the
/// call is sent again when it may. A card setup has already taken its turn in the card-setup lane,
/// which answers a full lane with a wait instead of waiting; it still takes a slot.
/// </summary>
internal sealed class BudgetedPaymentProvider(IPaymentProvider psp, ITokenBuckets buckets, IInFlightSlots slots, PspBudget budget, TimeProvider clock) : IPaymentProvider
{
    public Task<string> CreateCustomer(Guid backerId, string idempotencyKey, CancellationToken ct) =>
        Call(PspCall.Customer, c => psp.CreateCustomer(backerId, idempotencyKey, c), ct);

    public Task<CardSetupIntent> StartCardSetup(string pspCustomerId, string idempotencyKey, CancellationToken ct) =>
        InFlight(c => psp.StartCardSetup(pspCustomerId, idempotencyKey, c), ct);

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
                return await InFlight(send, ct);
            }
            catch (PspRateLimitedException limited) when (!limited.Concurrency)
            {
                await buckets.Drain(limited.Global ? budget.Account : budget.Endpoint(call), ct);
            }
            catch (PspRateLimitedException)
            {
                // The slots were shrunk when the call ended; it is sent again when one is free.
            }
        }
    }

    private async Task<T> InFlight<T>(Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        var slot = await TakeSlot(ct);
        try
        {
            return await send(ct);
        }
        catch (PspRateLimitedException limited) when (limited.Concurrency)
        {
            await slots.Shrink(budget.Calls, CancellationToken.None);
            throw;
        }
        finally
        {
            // Given back even when the caller has given up, so a cancelled call never keeps its slot until the lease ends.
            await slots.Release(budget.Calls, slot, CancellationToken.None);
        }
    }

    private async Task<string> TakeSlot(CancellationToken ct)
    {
        while (true)
        {
            var slot = await slots.TryTake(budget.Calls, ct);
            if (slot.SlotId is { } id)
            {
                return id;
            }

            await Task.Delay(slot.RetryAfter, clock, ct);
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
