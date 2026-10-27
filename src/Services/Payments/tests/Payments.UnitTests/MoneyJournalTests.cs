using Payments.Domain;

namespace Payments.UnitTests;

/// <summary>The money rules of lesson s3-a07, checked on the journals alone.</summary>
public class MoneyJournalTests
{
    private static readonly Guid Campaign = Guid.NewGuid();

    private static readonly MoneyPolicy Policy = new(PlatformFeeBasisPoints: 500, ReserveBasisPoints: 1_000);

    private static Dictionary<string, long> Balances(IEnumerable<MoneyJournal> journals) =>
        journals.SelectMany(j => j.Lines).GroupBy(l => l.Account).ToDictionary(g => g.Key, g => g.Sum(l => l.AmountMinor));

    [Fact]
    public void Harbor_Lights_balances_to_the_cent()
    {
        // 1,000 charges of $60 at 2.9% + 30 cents ($2.04 each), and one refund before payout.
        var journals = Enumerable.Range(0, 1_000)
            .Select(i => MoneyJournal.ForCharge(Campaign, $"pi_{i}", 6_000, 204, "USD"))
            .Append(MoneyJournal.ForRefund(Campaign, "re_1", 6_000, "USD", afterPayout: false, reserveLeftMinor: 0))
            .ToList();
        var before = Balances(journals);
        var clearing = -before[Accounts.Clearing];
        var fees = before[Accounts.ProcessingFees];
        Assert.Equal(5_994_000, clearing);
        Assert.Equal(204_000, fees);

        var platformFee = Policy.PlatformFee(clearing);
        var creatorShare = clearing - platformFee - fees;
        var reserve = Policy.Reserve(creatorShare);
        journals.Add(MoneyJournal.ForSplit(Campaign, "USD", clearing, fees, platformFee).Value);
        journals.Add(MoneyJournal.ForReserve(Campaign, "USD", reserve).Value);
        journals.Add(MoneyJournal.ForPayout(Campaign, "USD", creatorShare - reserve).Value);

        Assert.Equal(299_700, platformFee);
        Assert.Equal(5_490_300, creatorShare);
        Assert.Equal(549_030, reserve);
        Assert.Equal(4_941_270, creatorShare - reserve);

        var after = Balances(journals);
        Assert.Equal(0, after[Accounts.Clearing]);
        Assert.Equal(0, after[Accounts.ProcessingFees]);
        Assert.Equal(0, after[Accounts.CreatorPayable]);
        Assert.Equal(848_730, after[Accounts.PspBalance]);
        Assert.Equal(-299_700, after[Accounts.PlatformFee]);
        Assert.Equal(-549_030, after[Accounts.CreatorReserve]);
        Assert.All(journals, j => Assert.Equal(0, j.Lines.Sum(l => l.AmountMinor)));
        Assert.Equal(0, after.Values.Sum());
    }

    [Fact]
    public void A_journal_that_does_not_sum_to_zero_is_refused()
    {
        var result = MoneyJournal.Create(Campaign, JournalKinds.Charge, "pi_x", "USD",
            [new(Accounts.PspBalance, 6_000), new(Accounts.Clearing, -5_999)]);

        Assert.False(result.IsSuccess);
        Assert.Equal("journal_unbalanced", result.Errors[0].Code);
    }

    [Fact]
    public void A_journal_needs_two_lines_that_are_not_zero()
    {
        var result = MoneyJournal.Create(Campaign, JournalKinds.Charge, "pi_x", "USD",
            [new(Accounts.PspBalance, 0), new(Accounts.Clearing, 0)]);

        Assert.Equal("journal_too_short", result.Errors[0].Code);
    }

    [Fact]
    public void A_refund_after_payout_comes_from_the_reserve_first_then_the_creator()
    {
        var refund = MoneyJournal.ForRefund(Campaign, "re_2", 6_000, "USD", afterPayout: true, reserveLeftMinor: 1_099);

        Assert.Equal(1_099, refund.Lines.Single(l => l.Account == Accounts.CreatorReserve).AmountMinor);
        Assert.Equal(4_901, refund.Lines.Single(l => l.Account == Accounts.CreatorPayable).AmountMinor);
        Assert.Equal(-6_000, refund.Lines.Single(l => l.Account == Accounts.PspBalance).AmountMinor);
    }

    [Theory]
    [InlineData(10_992, 1_000, 1_099)] // 1,099.2 rounds down
    [InlineData(5, 1_000, 1)] // 0.5 rounds away from zero
    [InlineData(5_994_000, 500, 299_700)]
    public void Shares_round_to_the_nearest_minor_unit(long amount, int basisPoints, long expected) =>
        Assert.Equal(expected, MoneyPolicy.Share(amount, basisPoints));
}

/// <summary>Reconciliation's comparison, on lists alone.</summary>
public class MoneyDifferenceTests
{
    [Fact]
    public void Matching_lists_have_no_difference() =>
        Assert.Empty(MoneyDifference.Between([new("pi_1", 6_000, 204), new("re_1", -6_000, 0)], [new("re_1", -6_000, 0), new("pi_1", 6_000, 204)]));

    [Fact]
    public void Every_kind_of_difference_is_named()
    {
        var differences = MoneyDifference.Between(
            ours: [new("pi_missing", 6_000, 204), new("pi_amount", 6_000, 204), new("pi_fee", 6_000, 204)],
            theirs: [new("pi_amount", 5_000, 204), new("pi_fee", 6_000, 999), new("pi_extra", 6_000, 204)]);

        Assert.Equal(
            [("pi_missing", MoneyDifference.MissingAtPsp), ("pi_amount", MoneyDifference.AmountDiffers), ("pi_fee", MoneyDifference.FeeDiffers), ("pi_extra", MoneyDifference.MissingInBooks)],
            differences.Select(d => (d.SourceRef, d.Kind)));
    }
}
