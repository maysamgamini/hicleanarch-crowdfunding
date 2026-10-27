using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;

namespace IntegrationTests;

/// <summary>
/// Lesson s5-l14: every service answers <c>GET /version</c> with its name and the release it runs, the
/// image tag the service module sets as <c>SERVICE_VERSION</c>; a program nobody set a version for says
/// <c>dev</c>. Anyone may ask, signed in or not; the gateway forwards only <c>/v1</c>.
/// </summary>
public sealed class ServiceVersionTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_service_reports_its_name_and_the_release_it_runs()
    {
        await using var host = database.StartHost(
            payments: web => web.UseSetting("SERVICE_VERSION", "3f2a9c1"),
            campaigns: web => web.UseSetting("SERVICE_VERSION", "77b0e4d"));

        var answers = new Dictionary<string, JsonElement>
        {
            ["pledges"] = await Version(host.CreateClient()),
            ["payments"] = await Version(host.Payments().CreateClient()),
            ["campaigns"] = await Version(host.Campaigns().Host.CreateClient()),
            ["notifications"] = await Version(host.Notifications().Host.CreateClient()),
        };

        Assert.Equal("3f2a9c1", answers["payments"].GetProperty("version").GetString());
        Assert.Equal("77b0e4d", answers["campaigns"].GetProperty("version").GetString());
        Assert.Equal("dev", answers["pledges"].GetProperty("version").GetString());
        Assert.Equal("dev", answers["notifications"].GetProperty("version").GetString());
        // Each names itself: four different programs.
        Assert.All(answers, a => Assert.Contains(a.Key, a.Value.GetProperty("service").GetString()!, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<JsonElement> Version(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/version", Ct);
}
