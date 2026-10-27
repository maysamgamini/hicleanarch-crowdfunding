using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using BuildingBlocks.Infrastructure.Identity;

using Npgsql;

namespace Notifications.Host.Tests;

/// <summary>Notifications' one endpoint, served by the Notifications service with the same sign-in as before.</summary>
public sealed class NotificationsServiceTests(TestHost service)
{
    [Fact]
    public async Task A_signed_in_user_turns_a_kind_of_email_off_and_a_stranger_cannot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = service.Start();
        var client = host.CreateClient();

        var anonymous = await client.PutAsJsonAsync("/v1/me/notifications/campaign_updates", new { enabled = false }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var userId = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            LocalTokens.Issue(TestHost.SigningKey, TestHost.ClientId, userId, Roles.Backer));
        var off = await client.PutAsJsonAsync("/v1/me/notifications/campaign_updates", new { enabled = false }, ct);
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);

        await using var connection = new NpgsqlConnection(service.Owner);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT enabled FROM notifications.notification_preferences WHERE user_id = @user AND kind = 'campaign_updates'", connection);
        command.Parameters.AddWithValue("user", userId);
        Assert.Equal(false, await command.ExecuteScalarAsync(ct));
    }
}
