using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;
using Payments.Infrastructure.Psp;

namespace IntegrationTests;

/// <summary>A card setup has a deadline: a slow PSP or an empty budget answers 409 in time, and leaves nothing behind.</summary>
public sealed class CardSetupDeadlineTests(Database database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Result<CardSetupStarted>> Start(IServiceProvider services, Guid backerId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send<StartCardSetup, CardSetupStarted>(new StartCardSetup(Guid.NewGuid(), backerId), Ct)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct); // without a deadline the call would wait far longer than this
    }

    [Fact]
    public async Task A_psp_that_does_not_answer_in_time_gives_the_backer_409_and_no_setup_row()
    {
        await using var host = database.StartHost();
        var given = new Scenario(database, host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var backerId = Guid.NewGuid();
        var slow = host.Payments().Services.GetRequiredService<FakePaymentProvider>().HoldCardSetups(); // never answers while the test runs

        var placed = await given.Backer(backerId).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges",
            new { amount_minor = 4_900, tier_id = campaign.TierIds[0], payment_method_id = (string?)null }, Ct)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(slow.Reached.IsCompleted); // the PSP was asked, and was too slow
        Assert.Equal(HttpStatusCode.Conflict, placed.StatusCode);
        Assert.Equal("card_setup_unavailable", (await placed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM payments.card_setups WHERE backer_id = @id", backerId));
        Assert.Equal(0L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledges WHERE backer_id = @id", backerId));
    }

    [Fact]
    public async Task An_empty_customers_budget_gives_409_within_the_deadline()
    {
        // One customer token, refilled every 1,000 seconds: the second new backer's CreateCustomer would wait for it.
        await using var host = database.StartHost().WithWebHostBuilder(web => web.UseSetting("Payments:Budget:CustomersPerSecond", "0.001"));

        var first = await Start(host.Services, Guid.NewGuid());
        var second = await Start(host.Services, Guid.NewGuid());

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal("psp_unavailable", second.Errors[0].Code);
    }
}
