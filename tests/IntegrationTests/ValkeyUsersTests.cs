using System.Text.RegularExpressions;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Valkey;

using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>
/// The service users' access strings, read from infra/env/valkey.tf and created in a Valkey container as
/// ElastiCache creates them: each can do its own work, and none can list or wipe what other services keep
/// (CQA-1516). Key patterns do not limit KEYS, SCAN or FLUSHALL, so those must be denied by command.
/// </summary>
public sealed partial class ValkeyUsersTests(Valkey valkey) : IClassFixture<Valkey>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AccessString(string user)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CrowdFunding.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var terraform = File.ReadAllText(Path.Combine(root, "infra", "env", "valkey.tf"));
        return Regex.Match(terraform, $@"^\s*{user}\s*=\s*""(?<access>[^""]+)""", RegexOptions.Multiline).Groups["access"].Value;
    }

    /// <summary>The user created with its Terraform access string and a password, and a connection signed in as it.</summary>
    private async Task<ConnectionMultiplexer> SignIn(string user, string options = "")
    {
        await using (var admin = await ConnectionMultiplexer.ConnectAsync(valkey.Configuration + ",allowAdmin=true"))
        {
            var access = AccessString(user);
            Assert.False(string.IsNullOrEmpty(access), $"No access string for {user} in valkey.tf.");
            await admin.GetDatabase().ExecuteAsync("ACL", ["SETUSER", user, "reset", ">test-password", .. access.Split(' ')]);
        }

        // allowAdmin lifts the client's own guard on FLUSHALL and the like: what is refused below, Valkey refuses.
        return await ConnectionMultiplexer.ConnectAsync($"{valkey.Configuration},allowAdmin=true,user={user},password=test-password{options}");
    }

    private static async Task Refused(IDatabase db, string command, params object[] args) =>
        Assert.Contains("NOPERM", (await Assert.ThrowsAsync<RedisServerException>(() => db.ExecuteAsync(command, args))).Message, StringComparison.Ordinal);

    [Fact]
    public async Task Payments_runs_its_buckets_and_flags_and_can_neither_list_nor_wipe_the_shared_valkey()
    {
        // The restricted options its connection string carries (valkey.tf).
        await using var payments = await SignIn("payments", ",tiebreaker=,configChannel=");
        var db = payments.GetDatabase();

        var grant = await new ValkeyTokenBuckets(payments, new DegradedTokenBuckets(1, TimeProvider.System), NullLogger<ValkeyTokenBuckets>.Instance)
            .TryTake([new Bucket($"psp:{Guid.NewGuid():N}", new BucketRate(10, 10))], Ct);
        Assert.True(grant.Granted);
        await db.StringSetAsync("payments:pause", "now");
        Assert.True(await db.KeyDeleteAsync("payments:pause"));

        await Refused(db, "FLUSHALL");
        await Refused(db, "FLUSHDB");
        await Refused(db, "KEYS", "*");
        await Refused(db, "SCAN", "0");
    }

    [Fact]
    public async Task The_gateway_keeps_its_own_keys_and_can_neither_list_nor_wipe_the_shared_valkey()
    {
        await using var gateway = await SignIn("gateway");
        var db = gateway.GetDatabase();

        // What the gateway's buckets do: a script on its own keys that reads Valkey's clock.
        var key = $"gateway:{Guid.NewGuid():N}";
        await db.ScriptEvaluateAsync("local t = redis.call('TIME'); return redis.call('HSET', KEYS[1], 'at', t[1])", [key]);
        Assert.True(await db.KeyExistsAsync(key));

        await Refused(db, "FLUSHALL");
        await Refused(db, "FLUSHDB");
        await Refused(db, "KEYS", "*");
        await Refused(db, "SCAN", "0");
    }

    // CQA-2114: @scripting holds SCRIPT FLUSH and FUNCTION FLUSH, which empty the script cache and the
    // functions every service shares. Each user still runs its scripts, and is refused both.
    [Theory]
    [InlineData("api", "pledges")]
    [InlineData("campaigns", "campaigns")]
    [InlineData("payments", "payments")]
    [InlineData("gateway", "gateway")]
    public async Task No_user_can_flush_the_shared_scripts_or_functions(string user, string prefix)
    {
        await using var connection = await SignIn(user, ",tiebreaker=,configChannel=");
        var db = connection.GetDatabase();

        var key = $"{prefix}:{Guid.NewGuid():N}";
        await db.ScriptEvaluateAsync("return redis.call('SET', KEYS[1], 'x')", [key]);
        Assert.True(await db.KeyExistsAsync(key));

        await Refused(db, "SCRIPT", "FLUSH");
        await Refused(db, "FUNCTION", "FLUSH");
    }
}
