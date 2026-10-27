using Microsoft.Extensions.Configuration;

namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// The switches that let modules move out (<c>Modules:&lt;Module&gt;:Mode</c>, <c>Calls</c>, lessons s4-l03 to
/// s5-l09) are gone since lesson s5-l10: every module runs in a service of its own. A program that still finds
/// any <c>Modules:*</c> setting refuses to start and names it, so a value left in a task definition or a
/// settings file is never ignored without a word.
/// </summary>
public static class RetiredSettings
{
    public const string Section = "Modules";

    public static void Refuse(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var left = configuration.GetSection(Section).AsEnumerable(makePathsRelative: false)
            .Where(kv => kv.Value is not null)
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (left.Count > 0)
        {
            throw new InvalidOperationException(
                $"These settings no longer exist (lesson s5-l10: every module runs in its own service): {string.Join(", ", left)}. " +
                "Remove them; Payments' address is Services:Payments:BaseAddress.");
        }
    }
}
