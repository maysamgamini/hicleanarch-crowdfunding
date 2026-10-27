using System.Text.RegularExpressions;

namespace Delivery.Tests;

/// <summary>What every workflow must do, whoever edits it next.</summary>
public partial class WorkflowTests
{
    public static TheoryData<string> AllWorkflows() => [.. Workflows.All];

    [Theory]
    [MemberData(nameof(AllWorkflows))]
    public void Every_action_is_pinned_to_a_commit(string workflow) =>
        // A tag can be moved to other code; a commit cannot (CQA-1013). The comment names the version.
        Assert.All(Workflows.UsedActions(workflow), action =>
            Assert.Matches(PinnedToCommit(), action));

    [Fact]
    public void Payments_deploys_with_its_own_role_and_the_monolith_with_the_other()
    {
        var payments = Repository.Read(Workflows.Payments);
        var monolith = Repository.Read(Workflows.Monolith);

        Assert.Contains("vars.AWS_PAYMENTS_DEPLOY_ROLE_ARN", payments, StringComparison.Ordinal);
        Assert.DoesNotContain("vars.AWS_DEPLOY_ROLE_ARN", payments, StringComparison.Ordinal);
        Assert.Contains("vars.AWS_DEPLOY_ROLE_ARN", monolith, StringComparison.Ordinal);
        Assert.DoesNotContain("vars.AWS_PAYMENTS_DEPLOY_ROLE_ARN", monolith, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sandbox_deploys_payments_before_the_monolith_rolls_and_destroys_it_first()
    {
        var sandbox = Repository.Read(".github/workflows/sandbox.yml");
        int At(string text) => sandbox.IndexOf(text, StringComparison.Ordinal) is var i and >= 0 ? i : throw new InvalidOperationException($"'{text}' is not in sandbox.yml.");

        Assert.True(At("deploy-monolith.sh prepare") < At("deploy-payments.sh"));
        Assert.True(At("deploy-payments.sh") < At("deploy-monolith.sh roll"));
        Assert.True(At("deploy-monolith.sh roll") < At("smoke.sh"));
        Assert.True(At("destroy-payments.sh") < At("tofu -chdir=infra/env destroy"));
    }

    public static TheoryData<string> WorkflowsAndScripts() =>
    [
        .. Workflows.All,
        .. Directory.GetFiles(Path.Combine(Repository.Root, "scripts"), "*.sh")
            .Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(Repository.Root, p).Replace('\\', '/')),
    ];

    [Theory]
    [MemberData(nameof(WorkflowsAndScripts))]
    public void Infrastructure_runs_on_OpenTofu_at_the_pinned_version(string file)
    {
        // Terraform 1.6 and later is BUSL-1.1; the course uses only open-source tools, so OpenTofu
        // (MPL-2.0). Every setup reads the one pinned version, so CI and a learner's laptop agree.
        var text = Repository.Read(file);

        Assert.DoesNotMatch(TerraformCommand(), text);
        Assert.DoesNotContain("hashicorp/setup-terraform", text, StringComparison.Ordinal);
        foreach (var setup in SetupOpenTofu().Matches(text).Cast<Match>())
        {
            Assert.Contains("tofu_version_file: .opentofu-version", setup.Value, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")]
    private static partial Regex PinnedToCommit();

    [GeneratedRegex(@"(^|[\s;(|&$])terraform\s", RegexOptions.Multiline)]
    private static partial Regex TerraformCommand();

    // The uses: line of opentofu/setup-opentofu and its with: block, up to the next step.
    [GeneratedRegex(@"uses:\s*opentofu/setup-opentofu@\S+.*?(?=\n\s*- |\z)", RegexOptions.Singleline)]
    private static partial Regex SetupOpenTofu();
}
