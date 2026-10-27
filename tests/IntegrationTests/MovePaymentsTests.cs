using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Payments.Application;
using Payments.Infrastructure.Cutover;
using Payments.Infrastructure.Persistence;
using Payments.Infrastructure.Psp;

using Testcontainers.PostgreSql;

namespace IntegrationTests;

/// <summary>
/// The rehearsal of lesson s4-l07, fence, clone, switch, on two PostgreSQL 17 containers: a shared
/// database of its own (fencing the suite's would stop every other test) and a second one for
/// Payments. Aurora's clone is replaced by pg_dumpall and pg_dump of the whole fenced database, so
/// the copy holds every module's schema and role, as a clone does; the sandbox rehearsal clones.
/// Its three databases start after the other tests, so they add nothing to those tests' timings.
/// </summary>
[Collection(nameof(MovePaymentsTests))]
public sealed class MovePaymentsTests(Valkey valkey) : IClassFixture<Valkey>, IAsyncLifetime
{
    private const string NewPassword = "payments_own_test";

    private readonly Database _shared = new();

    private readonly PostgreSqlContainer _own = new PostgreSqlBuilder("postgres:17").WithDatabase("crowdfunding").Build();

    private readonly Gate _gate = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Payments' login on its own database, with the password the switch gives it.</summary>
    private string OwnLogin => new NpgsqlConnectionStringBuilder(_own.GetConnectionString()) { Username = "payments_app", Password = NewPassword }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_shared.InitializeAsync().AsTask(), _own.StartAsync(Ct));
        await using var flags = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(valkey.Configuration);
        await flags.GetDatabase().KeyDeleteAsync(ValkeyPaymentsPause.Key);
    }

    public async ValueTask DisposeAsync()
    {
        await _shared.DisposeAsync();
        await _own.DisposeAsync();
    }

    [Fact]
    public async Task The_move_runs_to_the_end_and_a_webhook_cut_by_the_fence_lands_on_Payments_own_database()
    {
        using var shared = PaymentsOn(_shared.Payments, s => s.AddScoped<ICardSetups>(sp => new GatedCardSetups(sp.GetRequiredService<EfPayments>(), _gate)));
        using var command = Command();
        var given = new Scenario(_shared, shared);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        await SavedCard(shared, given, campaign);
        var (pledgeId, setupId) = await PledgeWithNewCard(given, campaign);
        var webhook = Psp(shared).CompleteSetup(setupId);

        // The webhook has written its inbox row and waits, inside its transaction, when the pause starts.
        _gate.Armed = true;
        var inFlight = Deliver(shared, webhook);
        await _gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(0, await Step(command, "pause"));
        var paused = await Deliver(shared, webhook);
        Assert.Equal(0, await Step(command, "fence"));
        var fencedAt = await Scalar<string>(_shared.Owner, "SELECT shobj_description(oid, 'pg_authid') FROM pg_roles WHERE rolname = 'payments_app'");
        Assert.Equal(0, await Step(command, "fence"));
        _gate.Open.SetResult();
        var cut = await inFlight;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, paused.StatusCode);
        Assert.Equal("30", paused.Headers.RetryAfter?.ToString());
        Assert.False(cut.IsSuccessStatusCode);
        Assert.Equal(fencedAt, await Scalar<string>(_shared.Owner, "SELECT shobj_description(oid, 'pg_authid') FROM pg_roles WHERE rolname = 'payments_app'"));
        Assert.Equal("STARTED", await Scalar<string>(_shared.Owner, $"SELECT status FROM payments.card_setups WHERE pledge_id = '{pledgeId}'"));
        Assert.Equal(0L, await Scalar<long>(_shared.Owner, $"SELECT count(*) FROM payments.webhook_inbox WHERE message_id = '{MessageId(webhook)}'"));

        await Clone();
        Assert.Equal(0, await Step(command, "prune"));
        Assert.Equal(0, await Step(command, "prune"));
        Assert.Equal(["payments"], await Strings(_own.GetConnectionString(), "SELECT nspname FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname NOT IN ('public', 'information_schema')"));
        Assert.Equal(["payments_app"], await Strings(_own.GetConnectionString(), "SELECT rolname FROM pg_roles WHERE rolname LIKE '%\\_app' OR rolname LIKE '%\\_jobs' OR rolname LIKE '%\\_read'"));
        Assert.Equal(0, await Step(command, "verify"));
        Assert.Equal(0, await Step(command, "switch"));
        Assert.Equal(0, await Step(command, "switch"));

        // The new tasks start with the new secret while the flag is still set.
        using var own = PaymentsOn(OwnLogin);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Deliver(own, webhook)).StatusCode);
        Assert.Equal(0, await Step(command, "resume"));
        Assert.Equal(1, await Step(command, "rollback"));
        var retried = await Deliver(own, webhook);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("applied", (await retried.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("received").GetString());
        Assert.Equal("SUCCEEDED", await Scalar<string>(_own.GetConnectionString(), $"SELECT status FROM payments.card_setups WHERE pledge_id = '{pledgeId}'"));
        Assert.Equal("STARTED", await Scalar<string>(_shared.Owner, $"SELECT status FROM payments.card_setups WHERE pledge_id = '{pledgeId}'"));
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId) == "ACTIVE");

        // The monolith's migrate step, its owner on the shared database, leaves Payments' old schema alone.
        await own.Services.GetRequiredService<DatabaseMigrator>().Run(_shared.Owner, Ct);
        Assert.Equal("login=False", await Rights(_shared.Owner, tablesOnly: true));

        Assert.Equal(0, await Step(command, "drop-old"));
        Assert.Equal(0, await Step(command, "drop-old"));
        Assert.Equal(0L, await Scalar<long>(_shared.Owner, "SELECT count(*) FROM pg_namespace WHERE nspname = 'payments'"));
        Assert.Equal(0L, await Scalar<long>(_shared.Owner, "SELECT count(*) FROM pg_roles WHERE rolname = 'payments_app'"));
    }

    [Fact]
    public async Task A_move_rolled_back_after_the_switch_leaves_the_shared_database_as_it_was_and_Payments_working()
    {
        using var shared = PaymentsOn(_shared.Payments);
        using var command = Command();
        var given = new Scenario(_shared, shared);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        await SavedCard(shared, given, campaign);
        var (pledgeId, setupId) = await PledgeWithNewCard(given, campaign);
        var before = await Rights(_shared.Owner);

        Assert.Equal(0, await Step(command, "pause"));
        Assert.Equal(0, await Step(command, "fence"));
        await Clone();
        Assert.Equal(0, await Step(command, "prune"));
        Assert.Equal(0, await Step(command, "verify"));
        Assert.Equal(0, await Step(command, "switch"));
        Assert.Equal(0, await Step(command, "rollback"));
        Assert.Equal(0, await Step(command, "rollback"));

        Assert.Equal(before, await Rights(_shared.Owner));
        Assert.Equal("login=False", (await Rights(_own.GetConnectionString())).Split(';')[0]);
        Assert.Equal(0L, await Scalar<long>(_own.GetConnectionString(), "SELECT count(*) FROM pg_stat_activity WHERE usename = 'payments_app'"));
        // The PSP retries until it is answered; the first try may meet a pooled connection the fence ended.
        var webhook = Psp(shared).CompleteSetup(setupId);
        await Scenario.Eventually(async () => (await Deliver(shared, webhook)).StatusCode == HttpStatusCode.OK);
        Assert.Equal("SUCCEEDED", await Scalar<string>(_shared.Owner, $"SELECT status FROM payments.card_setups WHERE pledge_id = '{pledgeId}'"));
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId) == "ACTIVE");
    }

    [Fact]
    public async Task A_status_changed_on_one_side_only_fails_the_digests_and_the_switch()
    {
        using var shared = PaymentsOn(_shared.Payments);
        using var command = Command();
        var given = new Scenario(_shared, shared);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var pledgeId = await SavedCard(shared, given, campaign);

        Assert.Equal(0, await Step(command, "pause"));
        Assert.Equal(0, await Step(command, "fence"));
        await Clone();
        Assert.Equal(0, await Step(command, "prune"));
        Assert.Equal(0, await Step(command, "verify"));
        await Execute(_own.GetConnectionString(), $"UPDATE payments.card_setups SET status = 'FAILED' WHERE pledge_id = '{pledgeId}'");

        Assert.Equal(1, await Step(command, "verify"));
        Assert.Equal(1, await Step(command, "switch"));
        Assert.Equal(
            await Scalar<long>(_shared.Owner, "SELECT count(*) FROM payments.card_setups"),
            await Scalar<long>(_own.GetConnectionString(), "SELECT count(*) FROM payments.card_setups"));
        Assert.Equal("login=False", (await Rights(_own.GetConnectionString())).Split(';')[0]);
        Assert.Equal(0, await Step(command, "rollback"));
    }

    /// <summary>A host on the shared database, with Payments' connection where the test puts it.</summary>
    private WebApplicationFactory<Program> PaymentsOn(string payments, Action<IServiceCollection>? services = null) =>
        _shared.StartHost(services).WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:Valkey", valkey.Configuration)
            .UseSetting("ConnectionStrings:Payments", payments));

    /// <summary>The task that runs move-payments: both owners, Payments' new login, the old schema kept for no time.</summary>
    private WebApplicationFactory<Program> Command() =>
        PaymentsOn(_shared.Payments).WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:PaymentsOwnOwner", _own.GetConnectionString())
            .UseSetting("ConnectionStrings:PaymentsOwn", OwnLogin)
            .UseSetting("Payments:Move:KeepOldFor", "00:00:00"));

    private static Task<int> Step(WebApplicationFactory<Program> command, string step) => command.Services.MovePayments(step, Ct);

    /// <summary>The stand-in for Aurora's clone: every role and the whole database, copied after the fence.</summary>
    private async Task Clone()
    {
        var roles = await _shared.Exec(["pg_dumpall", "-U", "postgres", "--roles-only"], Ct);
        var data = await _shared.Exec(["pg_dump", "-U", "postgres", "-d", "crowdfunding"], Ct);
        Assert.Equal(0, roles.ExitCode);
        Assert.Equal(0, data.ExitCode);
        await _own.CopyAsync(Encoding.UTF8.GetBytes(roles.Stdout + data.Stdout), "/tmp/clone.sql", ct: Ct);
        var restored = await _own.ExecAsync(["psql", "-U", "postgres", "-d", "crowdfunding", "-q", "-f", "/tmp/clone.sql"], Ct);
        Assert.Equal(0, restored.ExitCode);
    }

    /// <summary>A new-card pledge whose card is saved: rows in the customers, setups, inbox and outbox tables.</summary>
    private static async Task<Guid> SavedCard(WebApplicationFactory<Program> host, Scenario given, Scenario.Published campaign)
    {
        var (pledgeId, setupId) = await PledgeWithNewCard(given, campaign);
        Assert.Equal(HttpStatusCode.OK, (await Deliver(host, Psp(host).CompleteSetup(setupId))).StatusCode);
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId) == "ACTIVE");
        return pledgeId;
    }

    private static async Task<(Guid PledgeId, string SetupId)> PledgeWithNewCard(Scenario given, Scenario.Published campaign)
    {
        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = 6_000, tier_id = campaign.TierIds[0], payment_method_id = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var body = await placed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secret = body.GetProperty("setup").GetProperty("client_secret").GetString()!;
        return (Guid.ParseExact(body.GetProperty("pledge_id").GetString()!["plg_".Length..], "N"), secret[..secret.IndexOf("_secret_", StringComparison.Ordinal)]);
    }

    private static FakePaymentProvider Psp(WebApplicationFactory<Program> host) => host.Services.GetRequiredService<FakePaymentProvider>();

    private static Task<HttpResponseMessage> Deliver(WebApplicationFactory<Program> host, SignedWebhook webhook)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/psp") { Content = new StringContent(webhook.Body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.Header, webhook.Signature);
        return host.CreateClient().SendAsync(request, Ct);
    }

    private static string MessageId(SignedWebhook webhook) => JsonDocument.Parse(webhook.Body).RootElement.GetProperty("id").GetString()!;

    /// <summary>What Payments' role may do: log in, and each right on each table and sequence of its schema.</summary>
    private static async Task<string> Rights(string owner, bool tablesOnly = false)
    {
        var login = await Scalar<bool>(owner, "SELECT rolcanlogin FROM pg_roles WHERE rolname = 'payments_app'");
        var note = await Scalar<string?>(owner, "SELECT shobj_description(oid, 'pg_authid') FROM pg_roles WHERE rolname = 'payments_app'");
        var rights = await Strings(owner, """
            SELECT c.relname || ':' || p.privilege FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'payments'
            CROSS JOIN unnest(ARRAY['INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'USAGE', 'SELECT']) AS p(privilege)
            WHERE (c.relkind = 'r' AND p.privilege <> 'USAGE' AND has_table_privilege('payments_app', c.oid, p.privilege))
               OR (c.relkind = 'S' AND p.privilege IN ('USAGE', 'UPDATE') AND has_sequence_privilege('payments_app', c.oid, p.privilege))
            ORDER BY 1
            """);
        return tablesOnly
            ? $"login={login}" + string.Concat(rights.Where(r => !r.EndsWith(":SELECT", StringComparison.Ordinal)).Select(r => ";" + r))
            : $"login={login};note={note};{string.Join(",", rights)}";
    }

    private static async Task<T> Scalar<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull ? default! : (T)value!;
    }

    private static async Task<IReadOnlyList<string>> Strings(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Holds one webhook between its inbox row and its card setup's update, until the test opens it.</summary>
    private sealed class Gate
    {
        public volatile bool Armed;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedCardSetups(ICardSetups inner, Gate gate) : ICardSetups
    {
        public Task<Payments.Domain.CardSetup> Add(Payments.Domain.CardSetup setup, CancellationToken cancellationToken) => inner.Add(setup, cancellationToken);

        public async Task<Payments.Domain.CardSetup?> TryComplete(string pspSetupId, string paymentMethodRef, CancellationToken cancellationToken)
        {
            if (gate.Armed)
            {
                gate.Armed = false;
                gate.Reached.SetResult();
                await gate.Open.Task;
            }

            return await inner.TryComplete(pspSetupId, paymentMethodRef, cancellationToken);
        }

        public Task<bool> TryFail(string pspSetupId, CancellationToken cancellationToken) => inner.TryFail(pspSetupId, cancellationToken);
    }
}

[CollectionDefinition(nameof(MovePaymentsTests), DisableParallelization = true)]
public sealed class MovePaymentsRunAlone;
