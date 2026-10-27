namespace Delivery.Tests;

/// <summary>
/// Each deployable builds alone from its solution filter, and a service's build never pulls in another
/// service's module: across modules, only Contracts. Checked on the project files, where a dependency is
/// declared, so a new project reference fails here before any pipeline runs.
/// </summary>
public class ProjectGraphTests
{
    private const string PaymentsFilter = "Payments.slnf";
    private const string MonolithFilter = "Monolith.slnf";
    private const string ContractsFilter = "Contracts.slnf";
    private const string NotificationsFilter = "Notifications.slnf";
    private const string CampaignsFilter = "Campaigns.slnf";

    /// <summary>The tests that start or load both services (s4-l10); both pipelines run them.</summary>
    private const string SystemFilter = "System.slnf";

    private const string PaymentsClient = "src/Modules/Payments.Client/Payments.Client.csproj";

    /// <summary>This project reads files only; ci.yml runs it on every change.</summary>
    private const string ThisProject = "tests/Delivery.Tests/Delivery.Tests.csproj";

    /// <summary>
    /// The producer side of the contract tests: built from the Contracts' source (s4-l06) by the contracts
    /// job that comes first in both service pipelines, not by either service's filter.
    /// </summary>
    private const string ContractProducerTests = "tests/Contracts.ProducerTests/Contracts.ProducerTests.csproj";

    /// <summary>The shared platform beside the BuildingBlocks: the broker adapter and the remote-call pipeline (s4-l02, s4-l03).</summary>
    private static readonly string[] Platform = ["src/Messaging/", "src/RemoteCalls/"];

    public static TheoryData<string> Filters() => [PaymentsFilter, MonolithFilter, NotificationsFilter, CampaignsFilter, ContractsFilter, SystemFilter];

    [Theory]
    [MemberData(nameof(Filters))]
    public void A_filter_names_only_projects_of_the_solution(string filter) =>
        Assert.Subset(Repository.SolutionProjects(Repository.FilterSolution(filter)).ToHashSet(), Repository.FilterProjects(filter).ToHashSet());

    [Theory]
    [MemberData(nameof(Filters))]
    public void A_filter_holds_its_whole_project_graph_so_it_builds_alone(string filter)
    {
        var projects = Repository.FilterProjects(filter);

        Assert.Empty(Repository.Closure(projects).Except(projects));
    }

    [Fact]
    public void Every_project_is_built_by_a_service_pipeline()
    {
        var built = Repository.FilterProjects(PaymentsFilter).Union(Repository.FilterProjects(MonolithFilter)).Union(Repository.FilterProjects(NotificationsFilter)).Union(Repository.FilterProjects(CampaignsFilter))
            .Union(Repository.FilterProjects(SystemFilter)).Append(ThisProject).Append(ContractProducerTests);

        Assert.Empty(Repository.SolutionProjects().Except(built));
    }

    [Fact]
    public void The_contracts_filter_holds_every_contracts_project()
    {
        var contracts = Repository.SolutionProjects(Repository.FilterSolution(ContractsFilter)).Where(p => p.EndsWith(".Contracts.csproj", StringComparison.Ordinal));

        Assert.Subset(Repository.FilterProjects(ContractsFilter).ToHashSet(), contracts.ToHashSet());
    }

    [Fact]
    public void The_payments_service_reaches_another_module_only_through_its_contracts()
    {
        var graph = Repository.Closure(Repository.FilterProjects(PaymentsFilter));

        var foreign = graph.Where(p => p.StartsWith("src/", StringComparison.Ordinal)
            && !p.StartsWith("src/Services/Payments/", StringComparison.Ordinal)
            && !Platform.Any(shared => p.StartsWith(shared, StringComparison.Ordinal))
            && !p.EndsWith(".Contracts.csproj", StringComparison.Ordinal));
        Assert.Empty(foreign);
    }

    [Fact]
    public void The_monolith_reaches_no_service_host()
    {
        var graph = Repository.Closure(["src/Host/Host.csproj", "src/Gateway/Gateway.csproj"]);

        Assert.DoesNotContain(graph, p => p.StartsWith("src/Services/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_monolith_builds_nothing_of_payments_but_its_client()
    {
        // Since s4-l10: Payments' module and host are built, tested and deployed by Payments' pipeline alone.
        var graph = Repository.Closure(Repository.FilterProjects(MonolithFilter));

        Assert.DoesNotContain(graph, p => p.StartsWith("src/Services/", StringComparison.Ordinal));
        Assert.All(graph.Where(p => p.StartsWith("src/Modules/Payments", StringComparison.Ordinal)), p => Assert.Equal(PaymentsClient, p));
        Assert.DoesNotContain(graph, p => p.StartsWith("tests/Payments.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_monolith_builds_nothing_of_notifications()
    {
        // Since s5-l04: Notifications' module and host are built, tested and deployed by Notifications' pipeline alone.
        var graph = Repository.Closure(Repository.FilterProjects(MonolithFilter));

        Assert.DoesNotContain(graph, p => p.StartsWith("src/Services/Notifications/", StringComparison.Ordinal));
        Assert.DoesNotContain(graph, p => p.StartsWith("tests/Notifications.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_monolith_builds_nothing_of_campaigns_but_its_contracts()
    {
        // Since s5-l09: Campaigns' module and host are built, tested and deployed by Campaigns' pipeline alone.
        var graph = Repository.Closure(Repository.FilterProjects(MonolithFilter));

        Assert.DoesNotContain(graph, p => p.StartsWith("src/Modules/Campaigns/", StringComparison.Ordinal) && !p.EndsWith(".Contracts.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(graph, p => p.StartsWith("src/Services/Campaigns/", StringComparison.Ordinal));
        Assert.DoesNotContain(graph, p => p.StartsWith("tests/Campaigns.", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_project_the_system_tests_build_is_built_by_a_service_pipeline()
    {
        // So a change to any of them runs a pipeline, and every pipeline runs the system tests.
        var services = Repository.FilterProjects(PaymentsFilter).Union(Repository.FilterProjects(MonolithFilter)).Union(Repository.FilterProjects(NotificationsFilter))
            .Union(Repository.FilterProjects(CampaignsFilter));
        string[] systemTests = ["tests/IntegrationTests/IntegrationTests.csproj", "tests/ArchitectureTests/ArchitectureTests.csproj", "tests/Contracts.ConsumerTests/Contracts.ConsumerTests.csproj"];

        Assert.Empty(Repository.FilterProjects(SystemFilter).Except(services).Except(systemTests));
    }
}
