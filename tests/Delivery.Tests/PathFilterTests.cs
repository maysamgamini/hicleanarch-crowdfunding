namespace Delivery.Tests;

/// <summary>
/// The path filters follow what each image is built from, and what each pipeline's tests run. A change
/// under Payments' OpenTofu root runs Payments' pipeline only; a change to the monolith's own paths runs
/// the monolith's only; a change to shared code, a contract or the subscriptions file runs both. Since
/// s4-l10 the monolith compiles nothing of Payments but its client, so a change to the Payments module or
/// its host runs Payments' pipeline only, and a change to Payments.Client the monolith's only. The tests
/// that start both services (System.slnf) run in both pipelines, so either service is checked against
/// the other at the same commit.
/// </summary>
public class PathFilterTests
{
    public static TheoryData<string, bool, bool, bool, bool> Changes() => new()
    {
        // changed file, runs payments, runs pledges (the monolith until s5-l10), runs notifications, runs campaigns
        { "src/Services/Payments/Payments.Host/Program.cs", true, false, false, false },
        { "src/Services/Payments/Payments.Host/Dockerfile", true, false, false, false },
        { "infra/services/payments/service.tf", true, false, false, false },
        { "src/Services/Payments/tests/Payments.UnitTests/SettlementTests.cs", true, false, false, false },
        { "src/Platform/Messaging/SqsEventReader.cs", true, true, true, true },
        { "tests/Contracts.ProducerTests/SnapshotTests.cs", true, true, true, true },
        { "src/Platform/RemoteCalls/RemoteCalls.cs", false, true, false, false },
        { "Payments.slnf", true, false, false, false },
        { "src/Services/Payments/Payments.Application/StartCardSetup.cs", true, false, false, false },
        { "src/Services/Payments/Payments.Infrastructure/Migrations/Snapshot.cs", true, false, false, false },
        { "src/Services/Pledges/Payments.Client/RemoteStartCardSetup.cs", false, true, false, false },
        { "src/Services/Payments/Payments.Contracts/CardSetup.cs", true, true, true, true },
        { "System.slnf", true, true, true, true },
        { "src/Services/Pledges/Pledges.Contracts/PledgeChargeRequested.cs", true, true, true, true },
        { "src/Services/Campaigns/Campaigns.Contracts/CampaignPublished.cs", true, true, true, true },
        { "src/Platform/BuildingBlocks/BuildingBlocks.Application/Dispatcher.cs", true, true, true, true },
        { "events/subscriptions.json", true, true, true, true },
        { "Directory.Packages.props", true, true, true, true },
        { "infra/env/shared-outputs.tf", true, true, true, true },
        { "src/Services/Pledges/Pledges.Host/Program.cs", false, true, false, false },
        { "src/Gateway/Program.cs", false, false, false, false },
        { "src/Services/Pledges/Pledges.Application/PlacePledge.cs", false, true, false, false },
        { "src/Services/Notifications/Notifications/Templates.cs", false, false, true, false },
        { "infra/env/ecs.tf", false, false, false, false },
        { "infra/services/pledges/main.tf", false, true, false, false },
        { "src/Services/Pledges/Pledges.Host/Dockerfile", false, true, false, false },
        { "tests/IntegrationTests/PledgeTests.cs", true, true, true, true },
        { "tests/ArchitectureTests/MonolithRulesTests.cs", true, true, true, true },
        { "src/Services/Notifications/Notifications.Host/Program.cs", false, false, true, false },
        { "infra/services/notifications/main.tf", false, false, true, false },
        { "infra/env/notifications-network.tf", false, false, true, false },
        { "src/Services/Notifications/tests/Notifications.Host.Tests/NotificationsServiceTests.cs", false, false, true, false },
        { "Notifications.slnf", false, false, true, false },
        { "docs/cost.md", false, false, false, false },
        { "infra/bootstrap/main.tf", false, false, false, false },
        { "infra/modules/service/deployment.tf", true, true, true, true },
        { "infra/modules/service-signals/main.tf", true, true, true, true },
        { "scripts/deployment-landed.sh", true, true, true, true },
        { "src/Services/Campaigns/Campaigns.Host/Program.cs", false, false, false, true },
        { "infra/services/campaigns/main.tf", false, false, false, true },
        { "Campaigns.slnf", false, false, false, true },
        { "src/Services/Campaigns/Campaigns.Application/GetCampaign.cs", false, false, false, true },
    };

    public static TheoryData<string> Pipelines() => [Workflows.Payments, Workflows.Pledges, Workflows.Notifications, Workflows.Campaigns];

    [Theory]
    [MemberData(nameof(Changes))]
    public void A_change_runs_the_pipelines_whose_image_it_is_in(string changedFile, bool payments, bool monolith, bool notifications, bool campaigns)
    {
        Assert.Equal(campaigns, Workflows.Triggers(Workflows.Paths(Workflows.Campaigns, "push"), changedFile));
        Assert.Equal(payments, Workflows.Triggers(Workflows.Paths(Workflows.Payments, "push"), changedFile));
        Assert.Equal(monolith, Workflows.Triggers(Workflows.Paths(Workflows.Pledges, "push"), changedFile));
        Assert.Equal(notifications, Workflows.Triggers(Workflows.Paths(Workflows.Notifications, "push"), changedFile));
    }

    [Theory]
    [MemberData(nameof(Pipelines))]
    public void Pull_requests_and_pushes_filter_the_same_paths(string workflow)
    {
        Assert.NotEmpty(Workflows.Paths(workflow, "push"));
        Assert.Equal(Workflows.Paths(workflow, "push"), Workflows.Paths(workflow, "pull_request"));
    }

    [Theory]
    [InlineData(Workflows.Payments, "Payments.slnf")]
    [InlineData(Workflows.Pledges, "Pledges.slnf")]
    [InlineData(Workflows.Notifications, "Notifications.slnf")]
    [InlineData(Workflows.Campaigns, "Campaigns.slnf")]
    public void A_change_to_any_project_a_service_builds_runs_its_pipeline(string workflow, string filter)
    {
        var paths = Workflows.Paths(workflow, "push");

        Assert.All(Repository.FilterProjects(filter), project =>
            Assert.True(Workflows.Triggers(paths, project), $"{workflow} does not run when {project} changes."));
    }

    [Theory]
    [InlineData(Workflows.Payments)]
    [InlineData(Workflows.Pledges)]
    [InlineData(Workflows.Notifications)]
    [InlineData(Workflows.Campaigns)]
    public void Both_pipelines_run_the_tests_that_start_both_services(string workflow) =>
        Assert.Contains("run: dotnet test System.slnf", Repository.Read(workflow), StringComparison.Ordinal);

    [Theory]
    [InlineData(Workflows.Payments)]
    [InlineData(Workflows.Pledges)]
    [InlineData(Workflows.Notifications)]
    [InlineData(Workflows.Campaigns)]
    public void A_change_to_a_contract_runs_the_contract_tests_first(string workflow)
    {
        var text = Repository.Read(workflow);

        Assert.Contains("run: dotnet test Contracts.slnf", text, StringComparison.Ordinal);
        Assert.Contains("needs: contracts", text, StringComparison.Ordinal);
        Assert.True(Workflows.Triggers(Workflows.Paths(workflow, "push"), "src/Services/Payments/Payments.Contracts/PaymentMethodSaved.cs"));
    }
}
