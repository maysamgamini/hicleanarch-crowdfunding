using System.Xml.Linq;

namespace ArchitectureTests;

/// <summary>
/// A remote client is what a host that does not run a module uses to call it (lesson s4-l05). It may
/// know the module's Contracts and the shared remote-call pipeline, and nothing inside the module, so
/// the monolith can stop compiling Payments (lesson s4-l10) and keep its client.
/// </summary>
public class RemoteClientRulesTests
{
    [Fact]
    public void The_payments_client_references_only_the_payments_contracts_and_the_remote_call_pipeline()
    {
        var references = XDocument.Load(Path.Combine(Root(), "src", "Modules", "Payments", "Payments.Client", "Payments.Client.csproj"))
            .Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension(((string)r.Attribute("Include")!).Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Payments.Contracts", "RemoteCalls"], references);
    }

    private static string Root()
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
}
