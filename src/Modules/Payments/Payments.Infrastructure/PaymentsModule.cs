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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Payments.Application;
using Payments.Domain;
using Payments.Contracts;
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
        services.AddOptions<PspBudget>().Bind(configuration.GetSection("Payments:Budget"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PspBudget>>().Value);
        services.AddSingleton((configuration.GetSection("Payments:Dunning").Get<DunningOptions>() ?? new DunningOptions()).Policy);
        services.AddOptions<SettlementOptions>().Bind(configuration.GetSection("Payments:Settlement"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SettlementOptions>>().Value);
        services.AddSingleton<SettlementWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<SettlementWorker>());
        services.AddSingleton<IStopParticipant>(sp => sp.GetRequiredService<SettlementWorker>());
        services.AddHostedService<PaymentsMonitor>();
        services.AddSingleton<IModuleMigrator, PaymentsMigrator>();
        services.AddSingleton(new ModuleDatabase(PaymentsModuleName.Value, PaymentsDbContext.Schema, connectionString));
        services.AddOptions<PaymentsOptions>().Bind(configuration.GetSection("Payments"))
            .Validate(o => o.WebhookSecret.Length >= 16, "Payments:WebhookSecret must be at least 16 characters.");

        // The course runs on the fake PSP. A real adapter implements the same port. Every call goes
        // through the circuit first, then the shared budget (lesson s3-a04).
        services.AddSingleton<FakePaymentProvider>();
        services.AddSingleton(configuration.GetSection("Payments:Breaker").Get<CircuitBreakerOptions>() ?? new CircuitBreakerOptions());
        services.AddSingleton<CircuitBreaker>();
        services.AddSingleton<IPaymentProvider>(sp => new CircuitBreakingPaymentProvider(
            new BudgetedPaymentProvider(sp.GetRequiredService<FakePaymentProvider>(), sp.GetRequiredService<ITokenBuckets>(),
                sp.GetRequiredService<PspBudget>(), sp.GetRequiredService<TimeProvider>()),
            sp.GetRequiredService<CircuitBreaker>()));

        services.AddScoped<EfPayments>();
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
        app.MapGet("/v1/campaigns/{id}/settlement", async (string id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!PublicId.TryParse("cmp", id, out var campaignId))
            {
                return new[] { PaymentErrors.NoSettlement }.ToProblem();
            }

            var progress = await dispatcher.Query<GetSettlementProgress, SettlementProgress>(new GetSettlementProgress(campaignId), ct);
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
        var secret = services.GetRequiredService<IOptions<PaymentsOptions>>().Value.WebhookSecret;
        if (!WebhookSignature.Verify(secret, signature, body, services.GetRequiredService<TimeProvider>().GetUtcNow()))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The signature does not match.");
        }

        HandlePspWebhook command;
        try
        {
            using var message = JsonDocument.Parse(body);
            var root = message.RootElement;
            command = new HandlePspWebhook(root.GetProperty("id").GetString()!, root.GetProperty("type").GetString()!, root.GetProperty("data").Clone());
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
