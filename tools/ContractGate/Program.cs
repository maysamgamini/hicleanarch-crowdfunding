using ContractGate;

// scripts/contract-gate.sh runs this. Exit 0: allowed; 1: refused, with the reasons; 2: wrong arguments.
//   can-i-deploy --record <dir> --service <name> --version <sha> [--ecs <service>=<sha>]... [--first-adoption]
//                (--ecs <service>= with no version: ECS runs no version of that service, CQA-2121)
//   record       --record <dir> --service <name> --version <sha>
//   can-retire   --record <dir> --type <Producer>.<Name> [--command] [--database <connection string>]
// --repo <path> (default .) is the repository whose files are read at each version; can-retire reads
// events/retired.json at --version (default HEAD), and treats the type as an event unless --command says
// it is a command or query, so a forgotten flag errs on the side of the message lifetimes.

if (args.Length == 0)
{
    return Usage();
}

var options = Options.Parse(args[1..]);
var repository = new GitVersions(options.One("repo") ?? ".");
var record = options.One("record");
if (record is null)
{
    return Usage();
}

switch (args[0])
{
    case "can-i-deploy" when options.One("service") is { } service && options.One("version") is { } version:
        var ecs = options.Many("ecs").Select(e => e.Split('=', 2)).ToDictionary(e => e[0], e => e[1], StringComparer.Ordinal);
        return Report(CanIDeploy.Check(repository, DeploymentRecord.Read(record), service, version, options.Many("ecs").Any() ? ecs : null, options.Has("first-adoption")),
            $"{service} {version} may deploy");

    case "record" when options.One("service") is { } service && options.One("version") is { } version:
        var deployed = DeploymentRecord.Record(record, service, version, DateTimeOffset.UtcNow);
        Console.WriteLine($"{service}: running {deployed.Running}, previous {deployed.Previous ?? "none"}");
        return 0;

    case "can-retire" when options.One("type") is { } type:
        var facts = options.One("database") is { } database ? await Retirement.ReadOutbox(database, type, CancellationToken.None) : null;
        var kind = options.Has("command") ? ContractKind.CommandOrQuery : ContractKind.Event;
        return Report(Retirement.Check(repository, DeploymentRecord.Read(record), type, kind, options.One("version") ?? "HEAD", facts, DateTimeOffset.UtcNow),
            $"{type} may be removed");

    default:
        return Usage();
}

static int Report(GateResult result, string allowed)
{
    foreach (var note in result.Notes)
    {
        Console.WriteLine($"note: {note}");
    }

    foreach (var problem in result.Problems)
    {
        Console.Error.WriteLine($"refused: {problem}");
    }

    if (result.Passed)
    {
        Console.WriteLine(allowed);
    }

    return result.Passed ? 0 : 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: contract-gate can-i-deploy|record|can-retire --record <dir> ... (see Program.cs)");
    return 2;
}

namespace ContractGate
{
    /// <summary>--name value pairs; a name may repeat, and a flag stands alone.</summary>
    internal sealed class Options(ILookup<string, string?> values)
    {
        public static Options Parse(string[] args)
        {
            var pairs = new List<(string, string?)>();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = args[i][2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                pairs.Add((name, value));
            }

            return new Options(pairs.ToLookup(p => p.Item1, p => p.Item2, StringComparer.Ordinal));
        }

        public string? One(string name) => values[name].LastOrDefault();

        public IEnumerable<string> Many(string name) => values[name].OfType<string>();

        public bool Has(string name) => values.Contains(name);
    }
}
