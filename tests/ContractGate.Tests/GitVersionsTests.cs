using System.Diagnostics;

namespace ContractGate.Tests;

/// <summary>The gate reads each recorded version's files from git, as they were committed.</summary>
public sealed class GitVersionsTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "gate-repo-" + Guid.NewGuid().ToString("N"));

    public GitVersionsTests()
    {
        Directory.CreateDirectory(_repository);
        Git("init", "-q");
        Git("config", "user.email", "gate@example.test");
        Git("config", "user.name", "gate");
    }

    [Fact]
    public void A_file_is_read_as_it_was_at_each_commit()
    {
        var first = Commit(Release.ServicesPath, """{ "services": { "monolith": ["Pledges"] } }""");
        var second = Commit(Release.ServicesPath, """{ "services": { "pledges": ["Pledges"] } }""");
        var git = new GitVersions(_repository);

        Assert.Contains("monolith", git.Read(first, Release.ServicesPath), StringComparison.Ordinal);
        Assert.Contains("\"pledges\"", git.Read(second, Release.ServicesPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_did_not_exist_at_a_commit_reads_as_null()
    {
        var only = Commit("README.md", "nothing yet");

        Assert.Null(new GitVersions(_repository).Read(only, Release.ServicesPath));
    }

    public void Dispose() => Directory.Delete(_repository, recursive: true);

    private string Commit(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repository, path))!);
        File.WriteAllText(Path.Combine(_repository, path), text);
        Git("add", path);
        Git("commit", "-q", "-m", path);
        return Git("rev-parse", "HEAD").Trim();
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git", ["-C", _repository, .. args]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed");
        return output;
    }
}
