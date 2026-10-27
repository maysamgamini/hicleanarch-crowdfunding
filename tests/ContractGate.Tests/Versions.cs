using System.Text.Json.Nodes;

namespace ContractGate.Tests;

/// <summary>
/// Versions of the repository made in memory for a test. Each starts from the files checked in now (every
/// module's expectations and every producer's contract), moved to the four services of Stage 5's end state,
/// and a test changes what its story needs.
/// </summary>
internal sealed class Versions : IVersions
{
    private static readonly string Root = FindRoot();

    private static readonly string[] Modules = ["Campaigns", "Notifications", "Payments", "Pledges"];

    private readonly Dictionary<string, Dictionary<string, string>> _files = [];

    public string? Read(string version, string path) => _files[version].GetValueOrDefault(path);

    /// <summary>A new version, as checked in now and then changed by <paramref name="change"/>.</summary>
    public Versions Add(string version, Action<Files>? change = null)
    {
        var files = new Dictionary<string, string>
        {
            [Release.ServicesPath] = """{ "services": { "campaigns": ["Campaigns"], "notifications": ["Notifications"], "payments": ["Payments"], "pledges": ["Pledges"] } }""",
            [Retirement.RetiredPath] = """{ "retired": [] }""",
        };
        foreach (var module in Modules)
        {
            files[Expectations.PathOf(module)] = File.ReadAllText(Path.Combine(Root, Expectations.PathOf(module)));
            if (File.Exists(Path.Combine(Root, Release.ContractPath(module))))
            {
                files[Release.ContractPath(module)] = File.ReadAllText(Path.Combine(Root, Release.ContractPath(module)));
            }
        }

        // Stage 5's end state: by s5-l08 Campaigns no longer uses Pledges in process (the cancel and the totals moved).
        var stageFive = new Files(files);
        stageFive.Expectations("Campaigns", e => e["inProcess"] = new JsonArray());
        change?.Invoke(stageFive);
        _files[version] = files;
        return this;
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
}

/// <summary>The files of one version being made, edited as JSON.</summary>
internal sealed class Files(Dictionary<string, string> files)
{
    public void Contract(string module, Action<JsonObject> change) => Edit(Release.ContractPath(module), change);

    public void Expectations(string module, Action<JsonObject> change) => Edit(ContractGate.Expectations.PathOf(module), change);

    public void Services(string json) => files[Release.ServicesPath] = json;

    public void Retired(params string[] types) => files[Retirement.RetiredPath] = new JsonObject { ["retired"] = new JsonArray([.. types.Select(t => (JsonNode)t)]) }.ToJsonString();

    public void Remove(string path) => files.Remove(path);

    private void Edit(string path, Action<JsonObject> change)
    {
        var json = JsonNode.Parse(files[path])!.AsObject();
        change(json);
        files[path] = json.ToJsonString();
    }
}

/// <summary>Deployment records for a test, keyed by service.</summary>
internal static class Deployed
{
    public static IReadOnlyDictionary<string, DeployedService> Running(params (string Service, string Running, string? Previous)[] services) =>
        services.ToDictionary(s => s.Service, s => new DeployedService(s.Service, s.Running, s.Previous, DateTimeOffset.UnixEpoch), StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, DeployedService> AllAt(string version) =>
        Running(("campaigns", version, null), ("notifications", version, null), ("payments", version, null), ("pledges", version, null));
}
