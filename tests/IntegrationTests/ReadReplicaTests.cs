using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Pledges.Application;
using Pledges.Infrastructure.Totals;

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
    public async Task Page_reads_use_the_replica_and_my_pledge_reads_the_writer()
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
        var totals = await Database.Client(host).GetAsync($"/v1/campaigns/{published.Id}/totals", ct);
        var mine = await backer.GetAsync($"/v1/pledges/{pledgeId}", ct);

        // The catalog is loaded from the writer, because it may be cached (lesson s3-l03). The public
        // totals read the funding window from the "replica", which has not caught up: not found yet.
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, totals.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode); // the backer sees their own pledge
    }

    [Fact]
    public async Task The_aggregator_sums_the_ledger_on_the_replica_not_on_the_writer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var writerOnly = database.StartHost();
        var given = new Scenario(database, writerOnly);
        var published = await given.Publish();
        (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{published.Id}/pledges", Scenario.Pledge(6000), ct)).EnsureSuccessStatusCode();
        await using var host = database.StartHost().WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:PledgesRead", OnReplica(database.Pledges)));

        // The writer has the pledge; the "replica" has not caught up and does not know the campaign.
        async Task<DisplayTotals?> Aggregated(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> on)
        {
            on.Services.GetRequiredService<DirtyCampaigns>().Mark(published.CampaignId);
            for (var i = 0; i < 120; i++) // the aggregator computes a campaign at most every two seconds
            {
                if (await on.Services.GetRequiredService<ITotalsStore>().Get(published.CampaignId, ct) is { PledgedMinor: > 0 } totals)
                {
                    return totals;
                }

                await Task.Delay(50, ct);
            }

            return null;
        }

        Assert.Equal(6000, (await Aggregated(writerOnly))?.PledgedMinor); // reading the writer, it is found at once
        Assert.Null(await Aggregated(host));
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
