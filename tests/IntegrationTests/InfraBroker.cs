using System.Text.RegularExpressions;

namespace IntegrationTests;

/// <summary>
/// CQA-2115: each host gets only the topic and queue addresses its service's OpenTofu root gives its task
/// definition (<c>Events__Broker__Topics__*</c> and <c>Events__Broker__Queues__*</c> in
/// <c>infra/services/&lt;service&gt;/*.tf</c>), not every address the test broker has. A root that forgets a
/// consumer queue's ARN then fails here as it would in AWS: the host's start-up subscription check refuses
/// to start. Every other setting passes unchanged.
/// </summary>
internal static partial class InfraBroker
{
    private static readonly string Root = FindRoot();

    private static readonly Dictionary<string, HashSet<string>> Given = [];

    /// <summary>The settings of <paramref name="broker"/> that <paramref name="service"/>'s task definition has.</summary>
    public static IReadOnlyDictionary<string, string?> For(string service, IReadOnlyDictionary<string, string?> broker)
    {
        var given = Addresses(service);
        return broker.Where(kv => !IsAddress(kv.Key) || given.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>A topic's or a queue's address, which the infra gives per service.</summary>
    public static bool IsAddress(string key) =>
        key.StartsWith("Events:Broker:Queues:", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Events:Broker:Topics:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The configuration keys of the addresses the root sets, as the host reads them (<c>__</c> is <c>:</c>).</summary>
    public static IReadOnlySet<string> Addresses(string service)
    {
        lock (Given)
        {
            if (!Given.TryGetValue(service, out var keys))
            {
                var dir = Path.Combine(Root, "infra", "services", service);
                keys = new HashSet<string>(
                    Directory.GetFiles(dir, "*.tf").SelectMany(f => AddressSetting().Matches(File.ReadAllText(f)))
                        .Select(m => m.Groups["name"].Value.Replace("__", ":", StringComparison.Ordinal)),
                    StringComparer.OrdinalIgnoreCase);
                if (keys.Count == 0)
                {
                    throw new InvalidOperationException($"{dir} sets no topic or queue address.");
                }

                Given[service] = keys;
            }

            return keys;
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CrowdFunding.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (CrowdFunding.slnx) was not found.");
    }

    [GeneratedRegex(@"name\s*=\s*""(?<name>Events__Broker__(?:Queues|Topics)__[^""]+)""")]
    private static partial Regex AddressSetting();
}
