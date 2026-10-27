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
        Assert.True(At("destroy-payments.sh") < At("terraform -chdir=infra/env destroy"));
    }

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")]
    private static partial Regex PinnedToCommit();
}
