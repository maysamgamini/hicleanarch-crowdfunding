using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

/// <summary>
/// Lesson s5-l03's route: a user's notification preference goes to the Notifications service, ahead of
/// the catch-all; every other path, other methods on the same path included, still goes to the monolith.
/// </summary>
public sealed class NotificationsRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> Start(EchoService monolith, EchoService notifications) =>
        GatewayHost.Start(monolith.Address).WithWebHostBuilder(web => web
            .UseSetting("ReverseProxy:Clusters:notifications:Destinations:notifications:Address", notifications.Address));

    public static TheoryData<string, string, string> Paths() => new()
    {
        { "PUT", "/v1/me/notifications/campaign_updates", "notifications" },
        { "GET", "/v1/me/notifications/campaign_updates", "pledges" },
        { "PUT", "/v1/me/notifications", "pledges" },
        { "GET", "/v1/campaigns/cmp_1", "pledges" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task The_preference_reaches_notifications_and_every_other_path_the_monolith(string method, string path, string service)
    {
        await using var monolith = await EchoService.Start("pledges");
        await using var notifications = await EchoService.Start("notifications");
        await using var gateway = Start(monolith, notifications);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : new StringContent("{}") };
        request.Headers.Add("X-Forwarded-For", "203.0.113.21");

        var echo = await (await gateway.CreateClient().SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(service, echo.GetProperty("service").GetString());
    }
}
