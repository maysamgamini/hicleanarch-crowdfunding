using System.Security.Claims;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Identity;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Hosting;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Valkey;

using Campaigns.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Payments.Application;
using Payments.Contracts;
using Payments.Domain;
using Payments.Infrastructure.Persistence;
using Payments.Infrastructure.Psp;

using Pledges.Contracts;

namespace Payments.Infrastructure;

/// <summary>
/// Everything a host needs to run the Payments module: its services and its endpoints. The
/// monolith's host calls both; a host of its own, if Payments is ever split off, would call the same two.
/// </summary>
public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Payments")
            ?? throw new InvalidOperationException("ConnectionStrings:Payments is not set.");
        services.AddDbContext<PaymentsDbContext>((sp, db) => PaymentsDbOptions
            .Configure((DbContextOptionsBuilder<PaymentsDbContext>)db, connectionString)
            .AddInterceptors(sp.GetRequiredService<OutboxCommitInterceptor<PaymentsDbContext>>()));
        services.AddModuleEvents<PaymentsDbContext>(PaymentsModuleName.Value);
        services.AddPublishedEvent<PaymentMethodSaved>();
        services.AddPublishedEvent<PledgeChargeSucceeded>();
        services.AddPublishedEvent<PledgeChargeFailed>();
        services.AddPublishedEvent<CampaignSettled>();
        services.AddEventHandler<PledgeChargeRequested, CreateCharge>(PaymentsModuleName.Value);
        services.AddEventHandler<CampaignFundingDecided, CreateSettlementJob>(PaymentsModuleName.Value);
        services.AddSharedValkey(configuration);
        // Every section is checked when the program starts, so a wrong setting fails the deploy, not the first call.
        services.AddOptions<PspBudget>().Bind(configuration.GetSection("Payments:Budget"))
            .Validate(b => new[] { b.AccountPerSecond, b.CustomersPerSecond, b.CardSetupsPerSecond, b.ChargesPerSecond, b.LookUpsPerSecond }.All(r => r > 0),
                "Payments:Budget: every rate must be positive.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PspBudget>>().Value);
        services.AddOptions<DunningOptions>().Bind(configuration.GetSection("Payments:Dunning"))
            .Validate(d => d.Window > TimeSpan.Zero && d.MaxAttempts > 0 && d.SoftRetries.All(r => r > TimeSpan.Zero && r < d.Window),
                "Payments:Dunning: the window and attempts must be positive, and every retry inside the window.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DunningOptions>>().Value.Policy);
        services.AddEventHandler<CampaignPublished, RecordPayee>(PaymentsModuleName.Value);
        services.AddOptions<MoneyOptions>().Bind(configuration.GetSection("Payments:Money"))
            .Validate(m => m.PlatformFeeBasisPoints is >= 0 and <= 10_000 && m.ReserveBasisPoints is >= 0 and <= 10_000
                    && m.PlatformFeeBasisPoints + m.ReserveBasisPoints <= 10_000 && m.PollEvery > TimeSpan.Zero,
                "Payments:Money: fee and reserve between 0 and 10,000 basis points together, and PollEvery positive.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MoneyOptions>>().Value);
        services.AddHostedService<MoneyWorker>();
        services.AddSingleton(sp =>
        {
            var money = sp.GetRequiredService<IOptions<MoneyOptions>>().Value;
            return new MoneyPolicy(money.PlatformFeeBasisPoints, money.ReserveBasisPoints);
        });
        services.AddOptions<SettlementOptions>().Bind(configuration.GetSection("Payments:Settlement"))
            .Validate(s => s.PollEvery > TimeSpan.Zero && s.BatchSize > 0 && s.PspCallTimeout > TimeSpan.Zero && s.Lease > s.PspCallTimeout,
                "Payments:Settlement: PollEvery, BatchSize and PspCallTimeout must be positive, and the lease longer than a PSP call.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SettlementOptions>>().Value);
        services.AddSingleton<SettlementWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<SettlementWorker>());
        services.AddSingleton<IStopParticipant>(sp => sp.GetRequiredService<SettlementWorker>());
        services.AddHostedService<PaymentsMonitor>();
        services.AddSingleton<IModuleMigrator, PaymentsMigrator>();
        services.AddSingleton(new ModuleDatabase(PaymentsModuleName.Value, PaymentsDbContext.Schema, connectionString));
        services.AddOptions<PaymentsOptions>().Bind(configuration.GetSection("Payments"))
            .Validate(o => o.WebhookSecret.Length >= 16, "Payments:WebhookSecret must be at least 16 characters.")
            .Validate(o => o.PreviousWebhookSecret is null or "" || o.PreviousWebhookSecret.Length >= 16,
                "Payments:PreviousWebhookSecret, when set, must be at least 16 characters.")
            .ValidateOnStart();

        // The course runs on the fake PSP. A real adapter implements the same port. Every call goes
        // through the circuit first, then the shared budget (lesson s3-a04).
        services.AddSingleton<FakePaymentProvider>();
        services.AddOptions<CircuitBreakerOptions>().Bind(configuration.GetSection("Payments:Breaker"))
            .Validate(o => o.FailureRatio is > 0 and <= 1 && o.MinimumThroughput >= 2 && o.SamplingDuration >= TimeSpan.FromMilliseconds(500)
                    && o.OpenFor >= TimeSpan.FromMilliseconds(500) && o.OpenJitter >= 0,
                "Payments:Breaker: FailureRatio in (0, 1], MinimumThroughput at least 2, SamplingDuration and OpenFor at least 500 ms.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CircuitBreakerOptions>>().Value);
        services.AddSingleton<CircuitBreaker>();
        services.AddSingleton<IPaymentProvider>(sp => new CircuitBreakingPaymentProvider(
            new BudgetedPaymentProvider(sp.GetRequiredService<FakePaymentProvider>(), sp.GetRequiredService<ITokenBuckets>(),
                sp.GetRequiredService<PspBudget>(), sp.GetRequiredService<TimeProvider>()),
            sp.GetRequiredService<CircuitBreaker>()));

        services.AddScoped<EfPayments>();
        services.AddScoped<EfMoney>();
        return services
            .AddPaymentsApplication()
            .AddScoped<IPspCustomers>(sp => sp.GetRequiredService<EfPayments>())
            .AddScoped<ICardSetups>(sp => sp.GetRequiredService<EfPayments>())
            .AddScoped<IWebhookInbox>(sp => sp.GetRequiredService<EfPayments>())
            .AddScoped<ISettlements, EfSettlements>()
            .AddScoped<IdempotentRequests<PaymentsDbContext>>()
            .AddSingleton<IdempotencyKeyPurge<PaymentsDbContext>>()
            .AddSingleton<IIdempotencyKeyPurge>(sp => sp.GetRequiredService<IdempotencyKeyPurge<PaymentsDbContext>>())
            .AddHostedService(sp => sp.GetRequiredService<IdempotencyKeyPurge<PaymentsDbContext>>())
            .AddScoped<IMoneyLedger>(sp => sp.GetRequiredService<EfMoney>())
            .AddScoped<IPayouts>(sp => sp.GetRequiredService<EfMoney>())
            .AddScoped<ICampaignPayees>(sp => sp.GetRequiredService<EfMoney>())
            .AddScoped<IChargeRefunds>(sp => sp.GetRequiredService<EfMoney>())
            .AddScoped<IChargeJournals>(sp => sp.GetRequiredService<EfMoney>())
            .AddScoped<IReconciliation>(sp => sp.GetRequiredService<EfMoney>())
            .AddScoped<IPayoutSteps>(sp => sp.GetRequiredService<EfMoney>())
            .AddScoped<ModuleTransaction<PaymentsDbContext>>()
            .AddKeyedScoped<ITransactionScope>(PaymentsModuleName.Value, (sp, _) => sp.GetRequiredService<ModuleTransaction<PaymentsDbContext>>());
    }

    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder app)
    {
        // The PSP calls this from the internet. It proves who it is with the signature, not a token.
        app.MapPost("/v1/webhooks/psp", async (HttpContext http, IServiceProvider services) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(http.RequestAborted);
            return await Receive(services, body, http.Request.Headers[WebhookSignature.Header], http.RequestAborted);
        }).AllowAnonymous().WithTags("Payments");

        // A charge in its recovery window, fixed by its backer (lesson s3-a04): a new card after a hard
        // decline, or a payment confirmed on our page when the bank wants the cardholder. Both are
        // changes, so both carry an Idempotency-Key: a retried confirmation gets the same payment back.
        app.MapPut("/v1/pledges/{id}/payment-method", (string id, NewCardRequest request, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<PaymentsDbContext> once) =>
            once.Run(http, user.UserId(), new { id, request }, async ct =>
            {
                if (!PublicId.TryParse("plg", id, out var pledgeId))
                {
                    return Result.Failure<Reply>(PaymentErrors.ChargeNotFound);
                }

                var result = await dispatcher.Send<UseNewCard, ChargeView>(new UseNewCard(pledgeId, user.UserId(), request.PaymentMethodId ?? ""), ct);
                return result.IsSuccess ? Result.Success(new Reply(StatusCodes.Status200OK, result.Value)) : Result.Failure<Reply>(result.Errors);
            })).RequireAuthorization(Policies.Backer).WithTags("Payments");

        app.MapPost("/v1/pledges/{id}/confirm-payment", (string id, ClaimsPrincipal user, HttpContext http,
            IDispatcher dispatcher, IdempotentRequests<PaymentsDbContext> once) =>
            once.Run(http, user.UserId(), new { id }, async ct =>
            {
                if (!PublicId.TryParse("plg", id, out var pledgeId))
                {
                    return Result.Failure<Reply>(PaymentErrors.ChargeNotFound);
                }

                var result = await dispatcher.Send<ConfirmPayment, PaymentToConfirm>(new ConfirmPayment(pledgeId, user.UserId()), ct);
                return result.IsSuccess ? Result.Success(new Reply(StatusCodes.Status200OK, result.Value)) : Result.Failure<Reply>(result.Errors);
            })).RequireAuthorization(Policies.Backer).WithTags("Payments");

        // How far a funded campaign's settlement has got, with an ETA at the PSP's rate (lesson s3-a04).
        // Only for the campaign's own creator (OWASP API1:2023); the policy only says the caller is a creator.
        app.MapGet("/v1/campaigns/{id}/settlement", async (string id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!PublicId.TryParse("cmp", id, out var campaignId))
            {
                return new[] { PaymentErrors.NoSettlement }.ToProblem();
            }

            var progress = await dispatcher.Query<GetSettlementProgress, SettlementProgress>(new GetSettlementProgress(campaignId, user.UserId()), ct);
            return progress.IsSuccess ? Results.Ok(progress.Value) : progress.Errors.ToProblem();
        }).RequireAuthorization(Policies.Creator).WithTags("Payments");

        // On a laptop there is no PSP to finish the card form: this plays the backer and the PSP.
        if (app.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            app.MapPost("/dev/fake-psp/setups/{setupId}/complete", async (string setupId, string? card, IServiceProvider services, CancellationToken ct) =>
            {
                var webhook = services.GetRequiredService<FakePaymentProvider>().CompleteSetup(setupId, card ?? FakePaymentProvider.CardSucceeds);
                return await Receive(services, webhook.Body, webhook.Signature, ct);
            }).AllowAnonymous().WithTags("Development");
        }

        return app;
    }

    internal static async Task<IResult> Receive(IServiceProvider services, string body, string? signature, CancellationToken ct)
    {
        var secrets = services.GetRequiredService<IOptions<PaymentsOptions>>().Value.WebhookSecrets;
        if (!WebhookSignature.Verify(secrets, signature, body, services.GetRequiredService<TimeProvider>().GetUtcNow()))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The signature does not match.");
        }

        HandlePspWebhook command;
        try
        {
            using var message = JsonDocument.Parse(body);
            var root = message.RootElement;
            command = new HandlePspWebhook(root.GetProperty("id").GetString()!, root.GetProperty("type").GetString()!, root.GetProperty("data").Clone());
            if (!HandlePspWebhook.IsReadable(command.Type, command.Data))
            {
                throw new JsonException($"A {command.Type} message without the fields it needs.");
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The message is not one we understand.");
        }

        var dispatcher = services.GetRequiredService<IDispatcher>();
        var handled = await services.GetRequiredService<ModuleTransaction<PaymentsDbContext>>()
            .Run(c => dispatcher.Send<HandlePspWebhook, WebhookHandled>(command, c), ct);
        return Results.Ok(new { received = handled.Value.ToString().ToLowerInvariant() });
    }
}

/// <summary>The JSON body of a new card for a charge in its recovery window: the PSP's reference to a saved card.</summary>
internal sealed record NewCardRequest(string? PaymentMethodId);
