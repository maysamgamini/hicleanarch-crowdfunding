using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Reads go to the reader endpoint, a backer's own pledge to the writer (lesson s3-l02). The test
/// stands in for a replica that is far behind: a second database, migrated but empty, as the
/// modules' read side. Whatever a request reads from there, it cannot see what was just written.
/// </summary>
public sealed class ReadReplicaTests(Database database) : IAsyncLifetime
{
    private const string ReplicaName = "crowdfunding_replica";

    public async ValueTask InitializeAsync()
    {
        await using (var owner = new NpgsqlConnection(database.Owner))
        {
            await owner.OpenAsync();
            await using var exists = new NpgsqlCommand($"SELECT 1 FROM pg_database WHERE datname = '{ReplicaName}'", owner);
            if (await exists.ExecuteScalarAsync() is not null)
            {
                return;
            }

            await using var create = new NpgsqlCommand($"CREATE DATABASE {ReplicaName}", owner);
            await create.ExecuteNonQueryAsync();
        }

        await using var host = database.StartHost();
        await host.Services.GetRequiredService<DatabaseMigrator>().Run(OnReplica(database.Owner), CancellationToken.None);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_campaign_page_reads_the_replica_and_my_pledge_reads_the_writer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var writerOnly = database.StartHost();
        var published = await new Scenario(database, writerOnly).Publish();
        await using var host = database.StartHost().WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:CampaignsRead", OnReplica(database.Campaigns))
            .UseSetting("ConnectionStrings:PledgesRead", OnReplica(database.Pledges)));
        var backer = new Scenario(database, host).Backer(Guid.NewGuid());
        var pledged = await backer.PostAsJsonAsync($"/v1/campaigns/{published.Id}/pledges", Scenario.Pledge(6000), ct);
        Assert.Equal(HttpStatusCode.Created, pledged.StatusCode);
        var pledgeId = (await pledged.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("pledge_id").GetString();

        var page = await Database.Client(host).GetAsync($"/v1/campaigns/{published.Id}", ct);
        var mine = await backer.GetAsync($"/v1/pledges/{pledgeId}", ct);

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);   // the "replica" has not caught up
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);         // the backer sees their own pledge
    }

    [Fact]
    public async Task The_read_side_refuses_writes_even_on_the_writer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(ReadSideRegistration.ReadOnly(database.Campaigns));
        await connection.OpenAsync(ct);
        await using var write = new NpgsqlCommand("UPDATE campaigns.campaigns SET title = title WHERE false", connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, refused.SqlState);
    }

    private static string OnReplica(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = ReplicaName }.ConnectionString;
}
