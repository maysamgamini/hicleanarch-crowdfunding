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

    /// <summary>This project reads files only; ci.yml runs it on every change.</summary>
    private const string ThisProject = "tests/Delivery.Tests/Delivery.Tests.csproj";

    /// <summary>
    /// The producer side of the contract tests: built from the Contracts' source (s4-l06) by the contracts
    /// job that comes first in both service pipelines, not by either service's filter.
    /// </summary>
    private const string ContractProducerTests = "tests/Contracts.ProducerTests/Contracts.ProducerTests.csproj";

    /// <summary>The shared platform beside the BuildingBlocks: the broker adapter and the remote-call pipeline (s4-l02, s4-l03).</summary>
    private static readonly string[] Platform = ["src/Messaging/", "src/RemoteCalls/"];

    public static TheoryData<string> Filters() => [PaymentsFilter, MonolithFilter, ContractsFilter];

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
        var built = Repository.FilterProjects(PaymentsFilter).Union(Repository.FilterProjects(MonolithFilter)).Append(ThisProject).Append(ContractProducerTests);

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
            && !p.StartsWith("src/Modules/Payments/", StringComparison.Ordinal)
            && !p.StartsWith("src/Services/Payments.Host/", StringComparison.Ordinal)
            && !Platform.Any(shared => p.StartsWith(shared, StringComparison.Ordinal))
            && !(p.StartsWith("src/Modules/", StringComparison.Ordinal) && p.EndsWith(".Contracts.csproj", StringComparison.Ordinal)));
        Assert.Empty(foreign);
    }

    [Fact]
    public void The_monolith_reaches_no_service_host()
    {
        // Until s4-l10 the monolith still compiles the Payments module for its in-process fallback;
        // what it must never do is depend on a service's host. Its filter does build Payments.Host, for
        // the integration tests that run both hosts (s4-l05); its deployables never reach it.
        var graph = Repository.Closure(["src/Host/Host.csproj", "src/Gateway/Gateway.csproj"]);

        Assert.DoesNotContain(graph, p => p.StartsWith("src/Services/", StringComparison.Ordinal));
    }
}
