using System.Diagnostics;

namespace Delivery.Tests;

/// <summary>
/// Lesson s5-l14: the deployment record changes only when the release really runs. <c>aws ecs wait
/// services-stable</c> also succeeds after a rollback, and ECS reports the rolled-back deployment
/// COMPLETED too, of the previous task definition; scripts/deployment-landed.sh tells them apart.
/// A stand-in <c>aws</c> answers as ECS would: no account, no calls.
/// </summary>
public sealed class DeploymentLandedTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("aws-standin").FullName;

    public void Dispose() => Directory.Delete(_bin, recursive: true);

    [Fact]
    public void A_completed_deployment_of_the_release_has_landed()
    {
        var (code, output) = Landed(rolloutState: "COMPLETED", runningTag: "new", reason: "ECS deployment ecs-svc/1 completed.");
        Assert.Equal(0, code);
        Assert.Contains("payments runs new.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rolled_back_deployment_has_not_landed_although_ECS_calls_it_completed()
    {
        var (code, output) = Landed(rolloutState: "COMPLETED", runningTag: "old",
            reason: "ECS deployment detected triggered alarm(s): rolling back to deploymentId ecs-svc/0.");
        Assert.Equal(1, code);
        Assert.Contains("payments runs old, not new: the release was rolled back.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_deployment_has_not_landed()
    {
        var (code, output) = Landed(rolloutState: "FAILED", runningTag: "new", reason: "ECS deployment circuit breaker: tasks failed to start.");
        Assert.Equal(1, code);
        Assert.Contains("the deployment is FAILED, not COMPLETED", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scripts/deploy-service.sh")]
    [InlineData("scripts/deploy-payments.sh")]
    public void Each_deploy_checks_that_the_release_landed_after_ECS_is_stable(string script)
    {
        var text = Repository.Read(script);
        var stable = text.IndexOf("aws ecs wait services-stable", StringComparison.Ordinal);
        var landed = text.IndexOf("deployment-landed.sh", StringComparison.Ordinal);
        Assert.True(stable > 0 && landed > stable, $"{script} must run deployment-landed.sh after the wait.");
    }

    [Theory]
    [InlineData("scripts/deploy-service.sh")]
    [InlineData("scripts/deploy-payments.sh")]
    public void The_first_apply_keeps_the_running_task_definition(string script)
    {
        // CQA-2103: the old image never starts with the release's settings.
        var applies = Repository.Read(script).Split('\n').Where(l => l.StartsWith("tf apply", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, applies.Count);
        Assert.Contains("service_task_definition=$running", applies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("service_task_definition", applies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".github/workflows/campaigns.yml")]
    [InlineData(".github/workflows/notifications.yml")]
    [InlineData(".github/workflows/payments.yml")]
    [InlineData(".github/workflows/pledges.yml")]
    public void Each_pipeline_gates_before_it_deploys_and_records_after(string workflow)
    {
        var text = Repository.Read(workflow);
        var check = text.IndexOf("mode: check", StringComparison.Ordinal);
        var deploy = text.IndexOf("./scripts/deploy-", StringComparison.Ordinal);
        var record = text.IndexOf("mode: record", StringComparison.Ordinal);
        Assert.True(check > 0 && deploy > check && record > deploy, $"{workflow}: can-i-deploy, then the deploy (which fails unless the release landed), then the record.");
    }

    private (int Code, string Output) Landed(string rolloutState, string runningTag, string reason)
    {
        var aws = Path.Combine(_bin, "aws");
        File.WriteAllText(aws, $$"""
            #!/usr/bin/env bash
            case "$2" in
              describe-services) echo '{"status":"PRIMARY","rolloutState":"{{rolloutState}}","rolloutStateReason":"{{reason}}","taskDefinition":"arn:aws:ecs:us-east-1:123456789012:task-definition/hicleanarch-payments:7"}' ;;
              describe-task-definition) echo '123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-payments:{{runningTag}}' ;;
              *) echo "unexpected: $*" >&2; exit 2 ;;
            esac
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(aws, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var start = new ProcessStartInfo("bash", [Path.Combine(Repository.Root, "scripts", "deployment-landed.sh"), "hicleanarch-sandbox", "payments", "new"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PATH"] = $"{_bin}:{Environment.GetEnvironmentVariable("PATH")}";
        using var script = Process.Start(start)!;
        var output = script.StandardOutput.ReadToEnd() + script.StandardError.ReadToEnd();
        script.WaitForExit();
        return (script.ExitCode, output);
    }
}
