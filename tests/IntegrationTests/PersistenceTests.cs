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
            var client = Database.Client(first, Database.Token(BuildingBlocks.Infrastructure.Identity.Roles.Creator));
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

    [Theory]
    [InlineData("campaigns")]
    [InlineData("pledges")]
    [InlineData("payments")]
    [InlineData("notifications")]
    public async Task A_module_role_cannot_write_its_schemas_migration_history(string schema)
    {
        var ct = TestContext.Current.CancellationToken;
        var role = schema switch
        {
            "campaigns" => database.Campaigns,
            "pledges" => database.Pledges,
            "payments" => database.Payments,
            _ => database.Notifications,
        };
        await using var connection = new NpgsqlConnection(role);
        await connection.OpenAsync(ct);
        await using var write = new NpgsqlCommand(
            $"INSERT INTO {schema}.__ef_migrations (migration_id, product_version) VALUES ('29991231000000_Sneaky', '10.0.0')", connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task A_module_role_has_no_use_of_the_public_schema_and_no_temporary_tables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var campaigns = new NpgsqlConnection(database.Campaigns);
        await campaigns.OpenAsync(ct);
        await using var rights = new NpgsqlCommand(
            "SELECT has_schema_privilege('public', 'USAGE'), has_database_privilege(current_database(), 'TEMPORARY')", campaigns);
        await using (var reader = await rights.ExecuteReaderAsync(ct))
        {
            Assert.True(await reader.ReadAsync(ct));
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }

        await using var temp = new NpgsqlCommand("CREATE TEMP TABLE sneaky (id int)", campaigns);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => temp.ExecuteNonQueryAsync(ct));
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
