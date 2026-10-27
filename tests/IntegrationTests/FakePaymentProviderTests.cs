using Microsoft.Extensions.Options;

using Payments.Application;
using Payments.Infrastructure;
using Payments.Infrastructure.Psp;

namespace IntegrationTests;

/// <summary>The course's fake PSP keeps its promises when tests call it from many threads at once.</summary>
public sealed class FakePaymentProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakePaymentProvider NewPsp() =>
        new(Options.Create(new PaymentsOptions { WebhookSecret = Database.WebhookSecret }), TimeProvider.System);

    private static Task<ChargeOutcome> Charge(FakePaymentProvider psp) =>
        psp.Charge(new ChargeRequest("cus_test", FakePaymentProvider.CardSucceeds, 1_000, "USD", $"plg_{Guid.NewGuid():N}", 1), Guid.NewGuid().ToString(), Ct);

    [Fact]
    public async Task Parallel_charges_after_LoseNextReplies_lose_exactly_that_many_replies()
    {
        var psp = NewPsp();
        psp.LoseNextReplies(7);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => Charge(psp), Ct)));

        Assert.Equal(7, outcomes.Count(o => o is ChargeOutcome.Unknown));
        Assert.Equal(200, psp.ChargesMade); // a lost reply is still a charge made
    }

    [Fact]
    public async Task A_LoseNextReplies_made_while_other_charges_run_is_never_undone_by_them()
    {
        var psp = NewPsp();
        var lost = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var chargers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (await Charge(psp) is ChargeOutcome.Unknown)
                {
                    Interlocked.Increment(ref lost);
                }
            }
        }, Ct)).ToArray();

        // Each round asks for one lost reply while the chargers run, and waits until a charger took it.
        // A request wiped by a charger that was between its two steps would never be taken; the old
        // code lost one within the first hundred rounds, so two thousand are enough without loading
        // the machine the rest of the suite shares.
        const int Rounds = 2_000;
        var taken = 0;
        while (taken < Rounds)
        {
            psp.LoseNextReplies(1);
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref lost) > taken, TimeSpan.FromSeconds(10)))
            {
                break;
            }

            taken++;
        }

        await stop.CancelAsync();
        await Task.WhenAll(chargers);
        Assert.Equal(Rounds, taken);
        Assert.Equal(Rounds, lost);
    }
}
