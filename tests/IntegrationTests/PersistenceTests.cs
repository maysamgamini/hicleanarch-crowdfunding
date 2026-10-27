using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;

namespace IntegrationTests;

public sealed class PersistenceTests(Database database)
{
    [Fact]
    public async Task A_draft_survives_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        string id;
        await using (var first = database.StartHost())
        {
            var client = first.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", Database.Token(BuildingBlocks.Infrastructure.Identity.Roles.Creator));
            var created = await client.PostAsJsonAsync("/v1/campaigns", new
            {
                title = "Harbor Lights",
                goal_minor = 5_000_000,
                currency = "USD",
                deadline = "2027-04-01T03:59:00Z",
                tiers = new[] { new { title = "Early Bird", min_pledge_minor = 4_900, limit = 500 } },
            }, ct);
            created.EnsureSuccessStatusCode();
            id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("campaign_id").GetString()!;
        }

        await using var second = database.StartHost();
        var read = await second.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{id}", ct);

        Assert.Equal("Harbor Lights", read.GetProperty("title").GetString());
        Assert.Equal("Early Bird", read.GetProperty("tiers")[0].GetProperty("title").GetString());
        Assert.Equal(500, read.GetProperty("tiers")[0].GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task The_campaigns_role_cannot_read_another_modules_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var owner = new NpgsqlConnection(database.Owner))
        {
            await owner.OpenAsync(ct);
            await using var create = new NpgsqlCommand(
                "CREATE SCHEMA IF NOT EXISTS other; CREATE TABLE IF NOT EXISTS other.secrets (id int);", owner);
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var campaigns = new NpgsqlConnection(database.Campaigns);
        await campaigns.OpenAsync(ct);
        await using var read = new NpgsqlCommand("SELECT count(*) FROM other.secrets", campaigns);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteScalarAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_campaigns_role_cannot_create_tables_of_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var campaigns = new NpgsqlConnection(database.Campaigns);
        await campaigns.OpenAsync(ct);
        await using var create = new NpgsqlCommand("CREATE TABLE campaigns.sneaky (id int)", campaigns);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }
}
