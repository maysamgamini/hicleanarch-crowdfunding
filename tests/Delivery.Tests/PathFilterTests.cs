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
    public static TheoryData<string, bool, bool, bool> Changes() => new()
    {
        // changed file, runs payments, runs monolith, runs notifications
        { "src/Services/Payments.Host/Program.cs", true, false, false },
        { "src/Services/Payments.Host/Dockerfile", true, false, false },
        { "infra/services/payments/service.tf", true, false, false },
        { "tests/Payments.UnitTests/SettlementTests.cs", true, false, false },
        { "src/Messaging/SqsEventReader.cs", true, true, true },
        { "tests/Contracts.ProducerTests/SnapshotTests.cs", true, true, true },
        { "src/RemoteCalls/RemoteCalls.cs", false, true, false },
        { "Payments.slnf", true, false, false },
        { "src/Modules/Payments/Payments.Application/StartCardSetup.cs", true, false, false },
        { "src/Modules/Payments/Payments.Infrastructure/Migrations/Snapshot.cs", true, false, false },
        { "src/Modules/Payments/Payments.Client/RemoteStartCardSetup.cs", false, true, false },
        { "src/Modules/Payments/Payments.Contracts/CardSetup.cs", true, true, true },
        { "System.slnf", true, true, true },
        { "src/Modules/Pledges/Pledges.Contracts/PledgeChargeRequested.cs", true, true, true },
        { "src/Modules/Campaigns/Campaigns.Contracts/CampaignPublished.cs", true, true, true },
        { "src/BuildingBlocks/BuildingBlocks.Application/Dispatcher.cs", true, true, true },
        { "events/subscriptions.json", true, true, true },
        { "Directory.Packages.props", true, true, true },
        { "infra/env/shared-outputs.tf", true, true, true },
        { "src/Host/Program.cs", false, true, false },
        { "src/Gateway/Program.cs", false, true, false },
        { "src/Modules/Pledges/Pledges.Application/PlacePledge.cs", false, true, false },
        { "src/Modules/Notifications/Templates.cs", false, false, true },
        { "infra/env/ecs.tf", false, true, false },
        { "Dockerfile", false, true, false },
        { "tests/IntegrationTests/PledgeTests.cs", true, true, true },
        { "tests/ArchitectureTests/MonolithRulesTests.cs", true, true, true },
        { "src/Services/Notifications.Host/Program.cs", false, false, true },
        { "infra/services/notifications/main.tf", false, false, true },
        { "infra/env/notifications-network.tf", false, true, true },
        { "tests/Notifications.Host.Tests/NotificationsServiceTests.cs", false, false, true },
        { "Notifications.slnf", false, false, true },
        { "docs/cost.md", false, false, false },
        { "infra/bootstrap/main.tf", false, false, false },
    };

    public static TheoryData<string> Pipelines() => [Workflows.Payments, Workflows.Monolith, Workflows.Notifications];

    [Theory]
    [MemberData(nameof(Changes))]
    public void A_change_runs_the_pipelines_whose_image_it_is_in(string changedFile, bool payments, bool monolith, bool notifications)
    {
        Assert.Equal(payments, Workflows.Triggers(Workflows.Paths(Workflows.Payments, "push"), changedFile));
        Assert.Equal(monolith, Workflows.Triggers(Workflows.Paths(Workflows.Monolith, "push"), changedFile));
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
    [InlineData(Workflows.Monolith, "Monolith.slnf")]
    [InlineData(Workflows.Notifications, "Notifications.slnf")]
    public void A_change_to_any_project_a_service_builds_runs_its_pipeline(string workflow, string filter)
    {
        var paths = Workflows.Paths(workflow, "push");

        Assert.All(Repository.FilterProjects(filter), project =>
            Assert.True(Workflows.Triggers(paths, project), $"{workflow} does not run when {project} changes."));
    }

    [Theory]
    [InlineData(Workflows.Payments)]
    [InlineData(Workflows.Monolith)]
    [InlineData(Workflows.Notifications)]
    public void Both_pipelines_run_the_tests_that_start_both_services(string workflow) =>
        Assert.Contains("run: dotnet test System.slnf", Repository.Read(workflow), StringComparison.Ordinal);

    [Theory]
    [InlineData(Workflows.Payments)]
    [InlineData(Workflows.Monolith)]
    [InlineData(Workflows.Notifications)]
    public void A_change_to_a_contract_runs_the_contract_tests_first(string workflow)
    {
        var text = Repository.Read(workflow);

        Assert.Contains("run: dotnet test Contracts.slnf", text, StringComparison.Ordinal);
        Assert.Contains("needs: contracts", text, StringComparison.Ordinal);
        Assert.True(Workflows.Triggers(Workflows.Paths(workflow, "push"), "src/Modules/Payments/Payments.Contracts/PaymentMethodSaved.cs"));
    }
}
