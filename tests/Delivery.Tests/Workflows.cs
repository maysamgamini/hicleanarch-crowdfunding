using System.Text.RegularExpressions;

namespace Delivery.Tests;

/// <summary>
/// The GitHub Actions workflows, read as text: the path filters of their triggers, and the actions they use.
/// </summary>
internal static partial class Workflows
{
    public const string Payments = ".github/workflows/payments.yml";
    public const string Monolith = ".github/workflows/monolith.yml";
    public const string Notifications = ".github/workflows/notifications.yml";
    public const string Campaigns = ".github/workflows/campaigns.yml";

    public static readonly string[] All = [.. Directory.GetFiles(Path.Combine(Repository.Root, ".github", "workflows"), "*.yml")
        .Order(StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(Repository.Root, p).Replace('\\', '/'))];

    /// <summary>The <c>paths</c> list of one trigger (<c>push</c>, <c>pull_request</c>) under <c>on:</c>.</summary>
    public static IReadOnlyList<string> Paths(string workflow, string trigger)
    {
        var lines = Repository.Read(workflow).Split('\n');
        var inOn = false;
        var inTrigger = false;
        var inPaths = false;
        var paths = new List<string>();
        foreach (var line in lines.Select(l => l.TrimEnd('\r')))
        {
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            if (indent == 0)
            {
                inOn = line == "on:";
                inTrigger = inPaths = false;
            }
            else if (inOn && indent == 2)
            {
                inTrigger = line.Trim() == trigger + ":";
                inPaths = false;
            }
            else if (inTrigger && indent == 4)
            {
                inPaths = line.Trim() == "paths:";
            }
            else if (inPaths && indent == 6 && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                paths.Add(line.TrimStart()[2..].Trim().Trim('"'));
            }
        }

        return paths;
    }

    /// <summary>
    /// Whether a changed file starts a workflow with these path filters, as GitHub decides it: <c>*</c>
    /// matches within a path segment, <c>**</c> across segments, and a later <c>!pattern</c> excludes again.
    /// </summary>
    public static bool Triggers(IEnumerable<string> filters, string changedFile)
    {
        var matched = false;
        foreach (var filter in filters)
        {
            var negated = filter.StartsWith('!');
            if (Glob(negated ? filter[1..] : filter).IsMatch(changedFile))
            {
                matched = !negated;
            }
        }

        return matched;
    }

    /// <summary>Every <c>uses:</c> of a workflow, local workflows and actions left out.</summary>
    public static IEnumerable<string> UsedActions(string workflow) =>
        UsesLine().Matches(Repository.Read(workflow)).Select(m => m.Groups["action"].Value)
            .Where(a => !a.StartsWith("./", StringComparison.Ordinal));

    private static Regex Glob(string pattern)
    {
        var regex = Regex.Escape(pattern)
            .Replace(@"\*\*", "\u0001", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\u0001", ".*", StringComparison.Ordinal);
        return new Regex($"^{regex}$", RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"^\s*(?:-\s+)?uses:\s*(?<action>\S+)", RegexOptions.Multiline)]
    private static partial Regex UsesLine();
}
