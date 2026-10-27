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
    public void Each_service_deploys_with_its_own_role_and_the_shared_layer_with_the_other()
    {
        // Since s5-l10 every deployable has its own pipeline and deploy role; no monolith is left.
        var shared = Repository.Read(Workflows.Shared);
        Assert.Contains("vars.AWS_DEPLOY_ROLE_ARN", shared, StringComparison.Ordinal);
        foreach (var (workflow, role) in new[] { (Workflows.Payments, "PAYMENTS"), (Workflows.Notifications, "NOTIFICATIONS"), (Workflows.Campaigns, "CAMPAIGNS"), (Workflows.Pledges, "PLEDGES") })
        {
            var text = Repository.Read(workflow);
            Assert.Contains($"vars.AWS_{role}_DEPLOY_ROLE_ARN", text, StringComparison.Ordinal);
            Assert.DoesNotContain("vars.AWS_DEPLOY_ROLE_ARN", text, StringComparison.Ordinal);
            Assert.DoesNotContain($"AWS_{role}_DEPLOY_ROLE_ARN", shared, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(Repository.Root, ".github", "workflows", "monolith.yml")));
        Assert.False(File.Exists(Path.Combine(Repository.Root, "scripts", "deploy-monolith.sh")));
    }

    [Fact]
    public void The_sandbox_deploys_the_shared_layer_then_payments_then_pledges_and_destroys_them_first()
    {
        var sandbox = Repository.Read(".github/workflows/sandbox.yml");
        int At(string text) => sandbox.IndexOf(text, StringComparison.Ordinal) is var i and >= 0 ? i : throw new InvalidOperationException($"'{text}' is not in sandbox.yml.");

        Assert.True(At("deploy-shared.sh") < At("hand-over.sh pledges"));
        Assert.True(At("hand-over.sh pledges") < At("deploy-payments.sh"));
        Assert.True(At("deploy-payments.sh") < At("deploy-service.sh pledges"));
        Assert.True(At("deploy-service.sh pledges") < At("smoke.sh"));
        Assert.True(At("destroy-service.sh pledges") < At("tofu -chdir=infra/env destroy"));
        Assert.True(At("destroy-payments.sh") < At("tofu -chdir=infra/env destroy"));
    }

    [Fact]
    public void Ci_checks_every_root_with_both_Terraform_and_OpenTofu()
    {
        // The course supports both tools: OpenTofu (MPL-2.0) by default, Terraform (BUSL-1.1) too. One
        // matrix job per tool, each at the version its file pins, runs the same checks on every root.
        var ci = Repository.Read(".github/workflows/ci.yml");
        var check = Repository.Read("scripts/check-infra.sh");

        Assert.Matches(IacMatrix(), ci);
        Assert.Contains("IAC: ${{ matrix.iac }}", ci, StringComparison.Ordinal);
        Assert.Contains("run: scripts/check-infra.sh", ci, StringComparison.Ordinal);
        Assert.Matches(SetupOpenTofu(), ci);
        Assert.Contains("tofu_version_file: .opentofu-version", SetupOpenTofu().Match(ci).Value, StringComparison.Ordinal);
        Assert.Matches(SetupTerraform(), ci);
        Assert.Contains("cat .terraform-version", ci, StringComparison.Ordinal);
        Assert.Matches(@"^\d+\.\d+\.\d+$", Repository.Read(".opentofu-version").Trim());
        Assert.Matches(@"^\d+\.\d+\.\d+$", Repository.Read(".terraform-version").Trim());

        Assert.Contains("iac=\"${IAC:-tofu}\"", check, StringComparison.Ordinal);
        foreach (var step in new[] { "fmt -check -recursive infra", "init -backend=false -input=false -lockfile=readonly", "validate", "test" })
        {
            Assert.Contains(step, check, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string> Scripts() =>
    [
        .. Directory.GetFiles(Path.Combine(Repository.Root, "scripts"), "*.sh")
            .Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(Repository.Root, p).Replace('\\', '/')),
    ];

    [Theory]
    [MemberData(nameof(Scripts))]
    public void A_script_runs_the_tool_IAC_names(string script)
    {
        // IAC=terraform runs any script with Terraform; unset, it is tofu. A script that called either
        // tool by name would ignore the choice.
        var code = Repository.Read(script).Split('\n').Where(l => !l.TrimStart().StartsWith('#'));
        Assert.All(code, line => Assert.DoesNotMatch(IacByName(), line));
    }

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")]
    private static partial Regex PinnedToCommit();

    [GeneratedRegex(@"(^|[\s;(|&{])(terraform|tofu)\s")]
    private static partial Regex IacByName();

    [GeneratedRegex(@"matrix:\s*\n\s*iac: \[tofu, terraform\]")]
    private static partial Regex IacMatrix();

    [GeneratedRegex(@"uses:\s*hashicorp/setup-terraform@[0-9a-f]{40}.*?(?=\n\s*- |\z)", RegexOptions.Singleline)]
    private static partial Regex SetupTerraform();

    // The uses: line of opentofu/setup-opentofu and its with: block, up to the next step.
    [GeneratedRegex(@"uses:\s*opentofu/setup-opentofu@\S+.*?(?=\n\s*- |\z)", RegexOptions.Singleline)]
    private static partial Regex SetupOpenTofu();
}
