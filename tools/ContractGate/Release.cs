using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ContractGate;

/// <summary>Files of the repository as they were at a version (a commit).</summary>
internal interface IVersions
{
    /// <summary>The file's text at <paramref name="version"/>, or null if it did not exist there.</summary>
    string? Read(string version, string path);
}

/// <summary>Reads files at a commit with <c>git show</c>.</summary>
internal sealed class GitVersions(string repository) : IVersions
{
    public string? Read(string version, string path)
    {
        var git = new ProcessStartInfo("git", ["-C", repository, "show", $"{version}:{path}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(git) ?? throw new InvalidOperationException("git could not be started.");
        var text = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? text : null;
    }
}

/// <summary>
/// What one version of the repository says about contracts: which modules each service runs
/// (deploy/services.json), what each module expects from the others, and each producer's contract.
/// </summary>
internal sealed record Release(
    string Version,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Services,
    IReadOnlyDictionary<string, Expectations?> Expectations,
    IReadOnlyDictionary<string, JsonObject> Contracts)
{
    public const string ServicesPath = "deploy/services.json";

    /// <summary>A module's contract snapshot at this commit: in its service's folder, Pledges' under src/Modules until s5-l12.</summary>
    public static string ContractPath(string module) =>
        module == "Pledges" ? $"src/Modules/{module}/{module}.Contracts/contract/contract.json" : $"src/Services/{module}/{module}.Contracts/contract/contract.json";

    /// <summary>Every place a module's contract has been, newest first: a recorded version may predate a move.</summary>
    public static IReadOnlyList<string> ContractPaths(string module) =>
        [$"src/Services/{module}/{module}.Contracts/contract/contract.json", $"src/Modules/{module}/{module}.Contracts/contract/contract.json"];

    public static Release Load(IVersions repository, string version) =>
        TryLoad(repository, version) ?? throw new InvalidDataException($"{version} has no {ServicesPath}; it predates the deploy gate.");

    /// <summary>The release at <paramref name="version"/>, or null for a version from before the deploy gate.</summary>
    public static Release? TryLoad(IVersions repository, string version)
    {
        if (repository.Read(version, ServicesPath) is not { } map)
        {
            return null;
        }

        var services = JsonNode.Parse(map)!["services"]!.AsObject()
            .ToDictionary(s => s.Key, s => (IReadOnlyList<string>)[.. s.Value!.AsArray().Select(m => (string)m!)], StringComparer.Ordinal);

        var modules = services.Values.SelectMany(m => m).Distinct(StringComparer.Ordinal).ToList();
        var expectations = modules.ToDictionary(
            m => m,
            m => ContractGate.Expectations.PathsOf(m).Select(path => repository.Read(version, path)).FirstOrDefault(json => json is not null) is { } json ? ContractGate.Expectations.Read(json) : null,
            StringComparer.Ordinal);
        var contracts = modules
            .Select(m => (Module: m, Json: ContractPaths(m).Select(path => repository.Read(version, path)).FirstOrDefault(json => json is not null)))
            .Where(c => c.Json is not null)
            .ToDictionary(c => c.Module, c => JsonNode.Parse(c.Json!)!.AsObject(), StringComparer.Ordinal);

        return new Release(version, services, expectations, contracts);
    }

    /// <summary>The service that runs <paramref name="module"/> in this version, if any.</summary>
    public string? ServiceOf(string module) => Services.FirstOrDefault(s => s.Value.Contains(module, StringComparer.Ordinal)).Key;

    public string Short => Version.Length > 7 ? Version[..7] : Version;

    /// <summary>Whether this version's producer still publishes the event "&lt;Producer&gt;.&lt;Name&gt;".</summary>
    public bool Publishes(string type) =>
        type.Split('.', 2) is [var producer, var name] && Contracts.TryGetValue(producer, out var contract) && contract["events"]?[name] is not null;
}

