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
