using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArchitectureTests;

/// <summary>
/// The four-services load profile (tests/load) describes the system as Terraform builds it: the
/// objectives name every service and an endpoint the routes know, the names CloudWatch is asked about
/// are the names the service module gives, and the cost estimate uses the task sizes each service's root runs.
/// </summary>
public class LoadProfileTests
{
    private static readonly string Root = FindRoot();

    private static readonly JsonObject Slos = Read("tests/load/slos.json")["services"]!.AsObject();

    private static readonly JsonObject Cost = Read("tests/load/cost-design.json");

    [Fact]
    public void Every_service_has_objectives()
    {
        Assert.Equal(["campaigns", "notifications", "payments", "pledges"], Slos.Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.All(Slos, s =>
        {
            var availability = (double)s.Value!["availability"]!;
            Assert.InRange(availability, 0.99, 0.99999);
            Assert.True((int)s.Value["latency"]!["p99Ms"]! > 0);
            Assert.True((int)s.Value["lagSeconds"]! > 0);
        });
    }

    [Fact]
    public void Each_objectives_endpoint_is_one_the_routes_send_to_that_service()
    {
        var routes = File.ReadAllText(Path.Combine(Root, "tests/load/lib/services.js"));

        Assert.All(Slos, s => Assert.Contains(
            $"service: \"{s.Key}\", endpoint: \"{(string)s.Value!["latency"]!["endpoint"]!}\"", routes, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("campaigns")]
    [InlineData("notifications")]
    [InlineData("payments")]
    [InlineData("pledges")]
    public void The_names_are_the_ones_the_service_module_gives_the_service(string service)
    {
        var module = File.ReadAllText(Path.Combine(Root, "infra/modules/service/main.tf"));
        var root = ServiceRoot(service);

        Assert.Contains("discovery_name = \"${var.name}-public\"", module, StringComparison.Ordinal);
        Assert.Contains($"name     = \"{service}\"", root, StringComparison.Ordinal);
        Assert.Equal($"{service}-public", (string)Slos[service]!["discoveryName"]!);
        Assert.Equal(service, (string)Slos[service]!["ecsService"]!);
    }

    [Theory]
    [InlineData("campaigns")]
    [InlineData("notifications")]
    [InlineData("payments")]
    [InlineData("pledges")]
    public void The_cost_estimate_uses_the_size_Terraform_runs(string service)
    {
        var root = ServiceRoot(service);
        var defaults = File.ReadAllText(Path.Combine(Root, "infra/modules/service/variables.tf"));
        int Size(string name) => int.Parse(
            (Setting(name).Match(root) is { Success: true } set ? set : Default(name).Match(defaults)).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var estimate = Cost["fourServices"]![service]!;

        Assert.Equal(Size("cpu") / 1024.0, (double)estimate["vcpu"]!);
        Assert.Equal(Size("memory") / 1024.0, (double)estimate["memoryGb"]!);
    }

    /// <summary>Every .tf file of the service's own root (infra/services/&lt;name&gt;), together.</summary>
    private static string ServiceRoot(string service) =>
        string.Concat(Directory.GetFiles(Path.Combine(Root, "infra/services", service), "*.tf").Order(StringComparer.Ordinal).Select(File.ReadAllText));

    private static Regex Setting(string name) => new($@"\n\s*{name}\s*=\s*(\d+)");

    private static Regex Default(string name) => new($@"variable ""{name}"" \{{\s*type\s*=\s*number\s*default\s*=\s*(\d+)");

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, path)))!.AsObject();

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
}
