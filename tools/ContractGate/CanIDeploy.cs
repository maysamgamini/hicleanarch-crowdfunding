namespace ContractGate;

/// <summary>
/// Whether one version of one service may deploy into an environment, given what the deployment record
/// says runs there. Both directions, against the running and the previous (rollback) version of every
/// other service:
/// <list type="number">
/// <item>as a producer, its contracts give every consumer what that consumer expects;</item>
/// <item>as a consumer, every producer's contract gives it what it expects;</item>
/// <item>no module uses another in process across two services.</item>
/// </list>
/// Modules of the same service are built and tested together, so the gate does not check them.
/// </summary>
internal static class CanIDeploy
{
    /// <param name="ecs">
    /// What ECS runs, per service, when the pipeline could ask; the record must agree. An empty version is a
    /// service ECS runs no version of (CQA-2121): not created yet, or destroyed.
    /// </param>
    /// <param name="firstAdoption">Skip, with a note, recorded versions from before the gate or without expectations files.</param>
    public static GateResult Check(
        IVersions repository,
        IReadOnlyDictionary<string, DeployedService> record,
        string service,
        string version,
        IReadOnlyDictionary<string, string>? ecs = null,
        bool firstAdoption = false)
    {
        var problems = new List<string>();
        var notes = new List<string>();

        if (ecs is not null)
        {
            foreach (var deployed in record.Values.Where(d => ecs.GetValueOrDefault(d.Service) is null))
            {
                problems.Add($"The record names {deployed.Service}, which ECS does not report: fix the record before deploying");
            }

            foreach (var deployed in record.Values.Where(d => ecs.GetValueOrDefault(d.Service) is ""))
            {
                problems.Add($"The record says {deployed.Service} runs {Short(deployed.Running)}, ECS runs no version of it: fix the record before deploying");
            }

            foreach (var (name, running) in ecs.Where(e => e.Value.Length > 0))
            {
                if (record.GetValueOrDefault(name)?.Running is { } recorded && recorded != running)
                {
                    problems.Add($"The record says {name} runs {Short(recorded)}, ECS runs {Short(running)}: fix the record before deploying");
                }
            }
        }

        var candidate = Release.Load(repository, version);
        if (!candidate.Services.TryGetValue(service, out var modules))
        {
            problems.Add($"{service} is not a service in {Release.ServicesPath} at {candidate.Short}");
            return new GateResult(problems, notes);
        }

        foreach (var module in modules.Where(m => candidate.Expectations[m] is null))
        {
            problems.Add($"{module} has no {Expectations.PathOf(module)} at {candidate.Short}");
        }

        foreach (var other in record.Values.Where(d => d.Service != service))
        {
            foreach (var (label, otherVersion) in other.Versions())
            {
                var where = $"{other.Service} ({label}, {Short(otherVersion)})";
                if (Release.TryLoad(repository, otherVersion) is not { } deployed)
                {
                    (firstAdoption ? notes : problems).Add($"{where}: predates {Release.ServicesPath}, so nothing it reads could be checked");
                    continue;
                }

                if (!deployed.Services.TryGetValue(other.Service, out var otherModules))
                {
                    problems.Add($"{where}: {other.Service} is not a service in its own {Release.ServicesPath}");
                    continue;
                }

                foreach (var consumer in otherModules)
                {
                    if (deployed.Expectations[consumer] is not { } expects)
                    {
                        (firstAdoption ? notes : problems).Add($"{where}: {consumer} has no expectations file, so nothing it reads could be checked");
                        continue;
                    }

                    foreach (var producer in modules.Where(candidate.Contracts.ContainsKey))
                    {
                        problems.AddRange(Compatibility.Problems(expects, producer, candidate.Contracts[producer]).Select(p => $"{where}: {p}"));
                    }

                    problems.AddRange(expects.InProcess.Where(t => modules.Contains(ModuleOf(t), StringComparer.Ordinal))
                        .Select(t => $"{where}: {consumer} uses {t} in process, but {ModuleOf(t)} runs in {service}"));
                }

                foreach (var consumer in modules.Where(m => candidate.Expectations[m] is not null))
                {
                    var expects = candidate.Expectations[consumer]!;
                    foreach (var producer in otherModules.Where(deployed.Contracts.ContainsKey))
                    {
                        problems.AddRange(Compatibility.Problems(expects, producer, deployed.Contracts[producer]).Select(p => $"against {where}: {p}"));
                    }

                    problems.AddRange(expects.InProcess.Where(t => otherModules.Contains(ModuleOf(t), StringComparer.Ordinal))
                        .Select(t => $"against {where}: {consumer} uses {t} in process, but {ModuleOf(t)} runs in {other.Service}"));
                }
            }
        }

        return new GateResult(problems.Distinct(StringComparer.Ordinal).ToList(), notes);
    }

    public static string ModuleOf(string type) => type.Split('.', 2)[0];

    private static string Short(string version) => version.Length > 7 ? version[..7] : version;
}

/// <summary>What a check found: problems refuse, notes only inform.</summary>
internal sealed record GateResult(IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool Passed => Problems.Count == 0;
}
