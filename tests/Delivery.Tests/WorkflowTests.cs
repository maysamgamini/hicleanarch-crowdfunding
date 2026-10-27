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

    private static readonly string[] ServiceNames = [.. Directory.GetDirectories(Path.Combine(Repository.Root, "src", "Services"))
        .Select(d => Path.GetFileName(d).ToLowerInvariant()).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Services() => [.. ServiceNames];

    /// <summary>
    /// CQA-2116: since s5-l10 every sandbox run builds the environment from nothing, so it must deploy every
    /// service, after the shared layer and its hand-over, before the smoke test; and destroy it, in the run
    /// and in the nightly sweep, before the shared layer. Until then the run deployed only Payments and Pledges,
    /// and the smoke test's first POST /v1/campaigns reached the catch-all, which answers 404.
    /// </summary>
    [Theory]
    [MemberData(nameof(Services))]
    public void The_sandbox_deploys_and_destroys_every_service(string service)
    {
        var sandbox = Repository.Read(".github/workflows/sandbox.yml");
        int At(string text) => sandbox.IndexOf(text, StringComparison.Ordinal) is var i and >= 0 ? i : throw new InvalidOperationException($"'{text}' is not in sandbox.yml.");
        var (deploy, destroy) = service == "payments"
            ? ("./scripts/deploy-payments.sh", "./scripts/destroy-payments.sh")
            : ($"./scripts/deploy-service.sh {service} ", $"./scripts/destroy-service.sh {service}");

        Assert.True(At("deploy-shared.sh") < At(deploy));
        Assert.True(At(deploy) < At("smoke.sh"));
        if (service != "payments")
        {
            // Payments' cluster is its own: its migrate step runs as that cluster's owner, with no hand-over.
            Assert.True(At($"hand-over.sh {service}") < At(deploy));
        }

        // Once in the run, once in the nightly sweep, each before the shared layer's destroy that follows it.
        var destroys = Regex.Matches(sandbox, Regex.Escape(destroy)).Select(m => m.Index).ToList();
        var shared = Regex.Matches(sandbox, Regex.Escape("tofu -chdir=infra/env destroy")).Select(m => m.Index).ToList();
        Assert.Equal(2, destroys.Count);
        Assert.Equal(2, shared.Count);
        Assert.True(destroys[0] < shared[0] && shared[0] < destroys[1] && destroys[1] < shared[1]);
    }

    [Fact]
    public void The_sandbox_turns_the_route_switches_on_only_after_every_service_runs_and_before_the_smoke_test()
    {
        var sandbox = Repository.Read(".github/workflows/sandbox.yml");
        int At(string text) => sandbox.IndexOf(text, StringComparison.Ordinal) is var i and >= 0 ? i : throw new InvalidOperationException($"'{text}' is not in sandbox.yml.");
        var lastDeploy = ServiceNames.Max(s => At(s == "payments" ? "./scripts/deploy-payments.sh" : $"./scripts/deploy-service.sh {s} "));

        foreach (var flag in new[] { "TF_VAR_campaigns_serves_routes", "TF_VAR_notifications_serves_preferences" })
        {
            Assert.True(At($"{flag}: \"false\"") < lastDeploy);
            var on = At($"{flag}: \"true\"");
            Assert.True(lastDeploy < on && on < At("smoke.sh"), $"{flag} is set before every service runs, or after the smoke test.");
        }

        // The second apply of the shared layer, with the switches on, comes after the last deploy too.
        Assert.True(sandbox.LastIndexOf("./scripts/deploy-shared.sh", StringComparison.Ordinal) > lastDeploy);
    }

    [Fact]
    public void A_shared_layer_deploy_that_sets_no_route_switch_keeps_the_last_ones()
    {
        // shared.yml deploys the gateway without the switches: they must not fall back to false (CQA-2116).
        var deploy = Repository.Read("scripts/deploy-shared.sh");
        var outputs = Repository.Read("infra/env/outputs.tf");
        foreach (var flag in new[] { "campaigns_serves_routes", "notifications_serves_preferences" })
        {
            Assert.Contains(flag, deploy, StringComparison.Ordinal);
            Assert.Contains($"output \"{flag}\"", outputs, StringComparison.Ordinal);
            Assert.DoesNotContain($"TF_VAR_{flag}", Repository.Read(Workflows.Shared), StringComparison.Ordinal);
        }

        Assert.Contains("tf output -raw \"$switch\"", deploy, StringComparison.Ordinal);
    }

    /// <summary>
    /// CQA-2117: <c>drop-moved-payments</c> needs the shared cluster's owner. Since s5-l10 Pledges' migrate step
    /// never has the owner's secret, so no deploy runs it; it runs, by hand, in Pledges' hand-over task, the one
    /// task with that secret and Pledges' image, through hand-over.sh, which overrides that task's container.
    /// </summary>
    [Fact]
    public void Drop_moved_payments_runs_in_pledges_hand_over_task_and_in_no_deploy()
    {
        var handover = Repository.Read("infra/env/pledges-network.tf");
        Assert.Contains("name      = \"hand-over\"", handover, StringComparison.Ordinal);
        Assert.Contains("{ name = \"ConnectionStrings__Owner\"", handover, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings__Owner", Repository.Read("infra/services/pledges/main.tf"), StringComparison.Ordinal);

        var script = Repository.Read("scripts/hand-over.sh");
        Assert.Contains("hand-over.sh pledges drop-moved-payments", script, StringComparison.Ordinal);
        Assert.Contains("CONTAINER=hand-over \"$(dirname \"$0\")/run-migration.sh\" infra/env \"$task\" \"$@\"", script, StringComparison.Ordinal);
        Assert.Contains("\\\"name\\\":\\\"${CONTAINER:-migrate}\\\"", Repository.Read("scripts/run-migration.sh"), StringComparison.Ordinal);

        // No script that deploys claims to run it.
        foreach (var deploy in Directory.GetFiles(Path.Combine(Repository.Root, "scripts"), "deploy-*.sh"))
        {
            Assert.DoesNotContain("drop-moved-payments", File.ReadAllText(deploy), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("after every deploy's migrate", Repository.Read("src/Services/Pledges/Pledges.Host/Program.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// CQA-2124: the first apply keeps the running task definition, which names the previous release's
    /// secrets, so both applies keep those secrets on the execution role; they leave one release later.
    /// </summary>
    [Theory]
    [InlineData("scripts/deploy-service.sh")]
    [InlineData("scripts/deploy-payments.sh")]
    public void A_deploy_keeps_the_previous_releases_secrets_on_the_execution_role(string script)
    {
        var lines = Repository.Read(script).Split('\n');
        Assert.Contains(lines, l => l.StartsWith("kept=$(tf output -json release_secret_arns", StringComparison.Ordinal));
        var applies = lines.Where(l => l.StartsWith("tf apply", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, applies.Count);
        Assert.All(applies, a => Assert.Contains("-var \"kept_secret_arns=$kept\"", a, StringComparison.Ordinal));
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
