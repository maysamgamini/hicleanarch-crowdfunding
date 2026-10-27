namespace BuildingBlocks.Domain;

/// <summary>
/// Ids as clients see them: a short prefix naming the kind of thing, an underscore, and the id's
/// 32 hex digits, such as <c>cmp_01a1…</c>. A module parses the ids it receives without knowing
/// another module's id types.
/// </summary>
public static class PublicId
{
    public static string Format(string prefix, Guid id) => $"{prefix}_{id:N}";

    public static bool TryParse(string prefix, string? text, out Guid id)
    {
        id = default;
        return text is not null
            && text.Length == prefix.Length + 33
            && text.StartsWith(prefix + "_", StringComparison.Ordinal)
            && Guid.TryParseExact(text.AsSpan(prefix.Length + 1), "N", out id);
    }
}
