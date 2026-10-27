using System.Diagnostics;

namespace Delivery.Tests;

/// <summary>
/// scripts/move-payments.sh run against fakes of <c>aws</c>, <c>terraform</c> and GNU <c>date</c>: every
/// command it would send is recorded and none reaches AWS. Each Terraform output is named after its root
/// (<c>env-cluster</c>, <c>payments-service</c>), so the calls show which service they are about.
/// </summary>
public sealed class MovePaymentsScriptTests : IDisposable
{
    private readonly string _fakes = Directory.CreateTempSubdirectory("move-payments-").FullName;

    public void Dispose() => Directory.Delete(_fakes, recursive: true);

    private string Calls => Path.Combine(_fakes, "calls.log");

    private void Fake(string name, string body)
    {
        var path = Path.Combine(_fakes, name);
        File.WriteAllText(path, "#!/usr/bin/env bash\n" + body.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private List<string> Run(params string[] arguments)
    {
        Fake("terraform", $$"""
            echo "terraform $*" >> '{{Calls}}'
            root=${1#-chdir=}; root=${root##*/}
            if [ "$2" = output ]; then printf '%s' "$root-$4"; fi
            """);
        Fake("aws", $$"""
            echo "aws $*" >> '{{Calls}}'
            case "$*" in
              *run-task*) echo task-arn ;;
              *exitCode*) echo 0 ;;
              *stoppedAt*) echo 100 ;;
              *LatestRestorableTime*) echo 200 ;;
              "rds describe-db-clusters"* | "secretsmanager describe-secret"*) exit 254 ;; # nothing of the clone is left
            esac
            """);
        // GNU date's -d, which the script needs (it runs on a Linux runner): the fake times are already seconds.
        Fake("date", """
            if [ "$1" = -u ] && [ "$2" = -d ]; then echo "$3"; else exec /bin/date "$@"; fi
            """);

        var start = new ProcessStartInfo("bash", [Path.Combine(Repository.Root, "scripts", "move-payments.sh"), .. arguments])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PATH"] = _fakes + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(_fakes, "summary.md");
        using var script = Process.Start(start)!;
        var output = script.StandardOutput.ReadToEndAsync();
        var errors = script.StandardError.ReadToEndAsync();
        Assert.True(script.WaitForExit(TimeSpan.FromSeconds(60)), "The script did not finish.");
        Assert.True(script.ExitCode == 0, $"exit {script.ExitCode}: {errors.Result}{output.Result}");
        return [.. File.ReadAllLines(Calls)];
    }

    private static int Index(List<string> calls, string call, int after = -1) =>
        calls.FindIndex(after + 1, c => c == call) is var i and >= 0 ? i : throw new InvalidOperationException($"No '{call}' after call {after}:\n{string.Join("\n", calls)}");

    [Fact]
    public void After_the_switch_both_services_are_stable_before_payments_resumes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The script runs on a Linux runner.");

        var calls = Run();

        var switched = calls.FindIndex(c => c.StartsWith("terraform -chdir=infra/env apply", StringComparison.Ordinal) && c.Contains("payments_database=own", StringComparison.Ordinal));
        Assert.True(switched >= 0, string.Join("\n", calls));
        var monolith = Index(calls, "aws ecs wait services-stable --cluster env-cluster --services env-service", switched);
        var payments = Index(calls, "aws ecs wait services-stable --cluster payments-cluster --services payments-service", switched);
        var resume = calls.FindIndex(switched, c => c.Contains("\"resume\"", StringComparison.Ordinal));
        Assert.True(resume > Math.Max(monolith, payments), string.Join("\n", calls));
    }

    [Fact]
    public void A_rollback_from_the_clone_waits_for_both_services_before_its_step()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The script runs on a Linux runner.");

        var calls = Run("--rollback");

        var back = calls.FindIndex(c => c.StartsWith("terraform -chdir=infra/env apply", StringComparison.Ordinal) && c.Contains("payments_database=shared", StringComparison.Ordinal));
        var step = calls.FindIndex(c => c.Contains("ecs run-task", StringComparison.Ordinal));
        Assert.True(back >= 0 && step > back, string.Join("\n", calls));
        Assert.InRange(Index(calls, "aws ecs wait services-stable --cluster env-cluster --services env-service", back), back, step);
        Assert.InRange(Index(calls, "aws ecs wait services-stable --cluster payments-cluster --services payments-service", back), back, step);
    }
}
