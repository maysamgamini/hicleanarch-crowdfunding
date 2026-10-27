using System.Text.Json;

namespace ContractGate;

/// <summary>
/// One service in one environment: the version it runs and the version it ran before, which is where ECS
/// rolls back to. Written by the service's pipeline only once ECS reports its deployment complete, so a
/// version that failed or rolled back is never recorded as running.
/// </summary>
internal sealed record DeployedService(string Service, string Running, string? Previous, DateTimeOffset RecordedAt)
{
    public IEnumerable<(string Label, string Version)> Versions()
    {
        yield return ("running", Running);
        if (Previous is not null && Previous != Running)
        {
            yield return ("previous", Previous);
        }
    }
}

/// <summary>
/// The deployment record of one environment: a directory with one file per service, &lt;service&gt;.json,
/// so two services deploying at once never write the same file. The pipelines keep it on the repository's
/// <c>deployments</c> branch, one directory per environment.
/// </summary>
internal static class DeploymentRecord
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IReadOnlyDictionary<string, DeployedService> Read(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json")
                .Select(f => JsonSerializer.Deserialize<DeployedService>(File.ReadAllText(f), Json)!)
                .ToDictionary(s => s.Service, StringComparer.Ordinal)
            : new Dictionary<string, DeployedService>();

    /// <summary>
    /// <paramref name="version"/> now runs <paramref name="service"/>; what ran before becomes the rollback
    /// target. Recording the running version again changes nothing.
    /// </summary>
    public static DeployedService Record(string directory, string service, string version, DateTimeOffset now)
    {
        var current = Read(directory).GetValueOrDefault(service);
        if (current?.Running == version)
        {
            return current;
        }

        var next = new DeployedService(service, version, current?.Running, now);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, service + ".json"), JsonSerializer.Serialize(next, Json) + "\n");
        return next;
    }
}
