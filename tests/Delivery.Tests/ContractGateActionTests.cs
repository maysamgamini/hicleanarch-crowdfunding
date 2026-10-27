using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Delivery.Tests;

/// <summary>
/// CQA-2121: the contract gate's check step asks ECS what each service runs. A service with no ECS service yet
/// (the sandbox's first deploy of it) or a destroyed one has no deployed version: the step passes it as
/// <c>&lt;name&gt;=</c> instead of failing on the task definition "None". The step's own shell runs here, with
/// <c>aws</c> and <c>scripts/contract-gate.sh</c> replaced by stubs, so nothing reaches AWS.
/// </summary>
public partial class ContractGateActionTests
{
    [Fact]
    public void A_service_ECS_does_not_run_is_passed_as_no_deployed_version()
    {
        var action = Repository.Read(".github/actions/contract-gate/action.yml");
        var script = string.Join('\n', CheckStep().Match(action).Groups["run"].Value.Split('\n').Select(l => l.Length >= 8 ? l[8..] : l.Trim()));
        Assert.Contains("describe-services", script, StringComparison.Ordinal);

        var dir = Directory.CreateTempSubdirectory("contract-gate-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            Directory.CreateDirectory(Path.Combine(dir, "scripts"));
            // ECS: payments runs abc; campaigns has no ACTIVE service (the query's [0] of nothing prints None).
            Stub(Path.Combine(dir, "bin", "aws"), """
                case "$*" in
                  *describe-services*--services\ campaigns*) echo None ;;
                  *describe-services*) echo arn:task/payments:3 ;;
                  *describe-task-definition*) echo 123.dkr.ecr/hicleanarch/crowdfunding-payments:abc ;;
                esac
                """);
            Stub(Path.Combine(dir, "scripts", "contract-gate.sh"), "echo \"gate $*\"");
            Stub(Path.Combine(dir, "step.sh"), "set -eo pipefail\n" + script.Replace("${{ inputs.environment }}", "sandbox", StringComparison.Ordinal)
                .Replace("${{ inputs.service }}", "campaigns", StringComparison.Ordinal).Replace("${{ inputs.version }}", "def", StringComparison.Ordinal));

            var start = new ProcessStartInfo("bash", [Path.Combine(dir, "step.sh")])
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["PATH"] = Path.Combine(dir, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["CLUSTER"] = "hicleanarch-sandbox";
            start.Environment["ECS_SERVICES"] = "campaigns=campaigns payments=payments";
            start.Environment["FIRST_ADOPTION"] = "false";
            start.Environment["RECORD_ROOT"] = dir;
            using var step = Process.Start(start)!;
            var output = step.StandardOutput.ReadToEnd() + step.StandardError.ReadToEnd();
            step.WaitForExit();

            Assert.True(step.ExitCode == 0, output);
            Assert.Contains("campaigns: no deployed version in hicleanarch-sandbox", output, StringComparison.Ordinal);
            Assert.Contains("--ecs campaigns= --ecs payments=abc", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Stub(string path, string body)
    {
        File.WriteAllText(path, "#!/usr/bin/env bash\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The can-i-deploy step's run: block, up to the next step.
    [GeneratedRegex(@"- name: can-i-deploy.*?run: \|\n(?<run>.*?)(?=\n    - name:)", RegexOptions.Singleline)]
    private static partial Regex CheckStep();
}
