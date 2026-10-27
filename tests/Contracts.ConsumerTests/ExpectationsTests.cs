using ContractGate;

namespace Contracts.ConsumerTests;

/// <summary>
/// Each module's expectations file (src/Services/&lt;Name&gt;/expectations.json, checked in) says what it needs
/// from the others. The producers' pipelines check their contracts against these files, and the deploy gate
/// reads them at every deployed version. The code must match its file; a change is accepted on purpose with
/// <c>UPDATE_CONTRACT_SNAPSHOTS=1 dotnet test tests/Contracts.ConsumerTests</c>.
/// </summary>
public class ExpectationsTests
{
    private static readonly string Root = FindRoot();

    public static TheoryData<string> Modules() => [.. ModuleExpectations.Modules];

    [Theory]
    [MemberData(nameof(Modules))]
    public void A_modules_expectations_match_the_file_beside_it(string module)
    {
        var current = ModuleExpectations.Of(module).Write();
        var path = Path.Combine(Root, Expectations.PathOf(module));
        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACT_SNAPSHOTS") == "1")
        {
            File.WriteAllText(path, current);
            return;
        }

        Assert.True(File.Exists(path), $"{Expectations.PathOf(module)} is missing; write it with UPDATE_CONTRACT_SNAPSHOTS=1.");
        Assert.True(File.ReadAllText(path) == current,
            $"What {module} needs from the other modules changed. If that is intended, rewrite {Expectations.PathOf(module)} with UPDATE_CONTRACT_SNAPSHOTS=1 and commit it: the producers' pipelines check their contracts against it.");
    }

    [Fact]
    public void Notifications_requires_the_backer_of_each_activated_pledge()
    {
        var expects = ModuleExpectations.Of("Notifications");

        Assert.Contains("backerId", expects.Events["Pledges.PledgeActivated"].Requires.Keys);
        Assert.Equal(1, expects.Events["Pledges.PledgeActivated"].Version);
    }

    [Fact]
    public void Pledges_calls_the_card_setup_endpoint_and_requires_its_client_secret()
    {
        var setup = ModuleExpectations.Of("Pledges").Endpoints["Payments.StartCardSetup"];

        Assert.Equal(["backerId", "pledgeId"], setup.Sends.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("clientSecret", setup.Requires.Keys);
    }

    [Fact]
    public void Campaigns_sends_Pledges_nothing_in_process() =>
        Assert.Empty(ModuleExpectations.Of("Campaigns").InProcess);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CrowdFunding.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (CrowdFunding.slnx) was not found.");
    }
}
