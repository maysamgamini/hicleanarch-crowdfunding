using BuildingBlocks.Domain;

namespace Campaigns.Domain;

/// <summary>An amount in the currency's minor unit (cents for USD), never a fraction.</summary>
public sealed record Money
{
    private Money(long amountMinor, string currency)
    {
        AmountMinor = amountMinor;
        Currency = currency;
    }

    public long AmountMinor { get; }

    /// <summary>An ISO 4217 code: three upper-case letters.</summary>
    public string Currency { get; }

    public static Result<Money> Create(long amountMinor, string currency)
    {
        if (amountMinor < 0)
        {
            return Result.Failure<Money>(CampaignErrors.NegativeAmount);
        }

        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
        {
            return Result.Failure<Money>(CampaignErrors.BadCurrency);
        }

        return Result.Success(new Money(amountMinor, currency));
    }
}
