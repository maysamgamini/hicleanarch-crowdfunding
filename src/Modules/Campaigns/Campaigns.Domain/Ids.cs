using System.Diagnostics.CodeAnalysis;

namespace Campaigns.Domain;

/// <summary>A campaign's id. Shown to clients as <c>cmp_</c> and 32 hex digits.</summary>
public readonly record struct CampaignId(Guid Value)
{
    private const string Prefix = "cmp_";

    public static CampaignId New() => new(Guid.CreateVersion7());

    public override string ToString() => $"{Prefix}{Value:N}";

    public static bool TryParse([NotNullWhen(true)] string? text, out CampaignId id)
    {
        id = default;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(text.AsSpan(Prefix.Length), "N", out var value))
        {
            return false;
        }

        id = new CampaignId(value);
        return true;
    }
}

/// <summary>A reward tier's id. Shown to clients as <c>tir_</c> and 32 hex digits.</summary>
public readonly record struct TierId(Guid Value)
{
    public static TierId New() => new(Guid.CreateVersion7());

    public override string ToString() => $"tir_{Value:N}";
}
