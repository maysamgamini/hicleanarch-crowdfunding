namespace ContractGate.Tests;

/// <summary>The record of what runs where: written once a deployment completes, read by every gate.</summary>
public sealed class DeploymentRecordTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "deployments-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_completed_deployment_makes_the_running_version_the_rollback_target()
    {
        DeploymentRecord.Record(_directory, "pledges", "v1", DateTimeOffset.UnixEpoch);
        DeploymentRecord.Record(_directory, "pledges", "v2", DateTimeOffset.UnixEpoch);

        var pledges = DeploymentRecord.Read(_directory)["pledges"];

        Assert.Equal(("v2", "v1"), (pledges.Running, pledges.Previous));
    }

    [Fact]
    public void Recording_the_running_version_again_keeps_the_rollback_target()
    {
        DeploymentRecord.Record(_directory, "pledges", "v1", DateTimeOffset.UnixEpoch);
        DeploymentRecord.Record(_directory, "pledges", "v2", DateTimeOffset.UnixEpoch);
        DeploymentRecord.Record(_directory, "pledges", "v2", DateTimeOffset.UnixEpoch);

        Assert.Equal("v1", DeploymentRecord.Read(_directory)["pledges"].Previous);
    }

    [Fact]
    public void Each_service_has_a_file_of_its_own_so_two_deployments_never_write_the_same_file()
    {
        DeploymentRecord.Record(_directory, "pledges", "v1", DateTimeOffset.UnixEpoch);
        DeploymentRecord.Record(_directory, "payments", "v7", DateTimeOffset.UnixEpoch);

        Assert.Equal(["payments.json", "pledges.json"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_service_never_deployed_has_no_rollback_target()
    {
        DeploymentRecord.Record(_directory, "notifications", "v1", DateTimeOffset.UnixEpoch);

        Assert.Equal([("running", "v1")], DeploymentRecord.Read(_directory)["notifications"].Versions());
    }

    [Fact]
    public void An_environment_with_no_record_yet_reads_as_empty() =>
        Assert.Empty(DeploymentRecord.Read(_directory));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
