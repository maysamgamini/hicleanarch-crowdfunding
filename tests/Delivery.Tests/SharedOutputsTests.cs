using System.Text.RegularExpressions;

namespace Delivery.Tests;

/// <summary>
/// Payments' OpenTofu root reads the shared layer's outputs from one JSON document. OpenTofu cannot check
/// a key of a decoded document before an apply, so this test does: every key Payments reads is published.
/// </summary>
public partial class SharedOutputsTests
{
    [Fact]
    public void Every_shared_value_payments_reads_is_published_by_the_shared_layer()
    {
        var published = PublishedKey().Matches(Repository.Read("infra/env/shared-outputs.tf")).Select(m => m.Groups["key"].Value).ToHashSet();
        var read = Directory.GetFiles(Path.Combine(Repository.Root, "infra", "services", "payments"), "*.tf")
            .SelectMany(f => ReadKey().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .ToHashSet();

        Assert.NotEmpty(read);
        Assert.Subset(published, read);
    }

    [GeneratedRegex(@"^\s{4}(?<key>\w+)\s+=", RegexOptions.Multiline)]
    private static partial Regex PublishedKey();

    [GeneratedRegex(@"local\.shared\.(?<key>\w+)")]
    private static partial Regex ReadKey();
}
