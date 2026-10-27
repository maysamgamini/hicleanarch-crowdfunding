using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Persistence;

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
        services.AddOptions<SettlementOptions>().Bind(configuration.GetSection("Payments:Settlement"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SettlementOptions>>().Value);
        services.AddHostedService<SettlementWorker>();
        services.AddHostedService<PaymentsMonitor>();
        services.AddSingleton<IModuleMigrator, PaymentsMigrator>();
        services.AddSingleton(new ModuleDatabase(PaymentsModuleName.Value, PaymentsDbContext.Schema, connectionString));
        services.AddOptions<PaymentsOptions>().Bind(configuration.GetSection("Payments"))
            .Validate(o => o.WebhookSecret.Length >= 16, "Payments:WebhookSecret must be at least 16 characters.");

        // The course runs on the fake PSP. A real adapter implements the same port.
        services.AddSingleton<FakePaymentProvider>();
        services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<FakePaymentProvider>());

        services.AddScoped<EfPayments>();
        return services
            .AddPaymentsApplication()
            .AddScoped<IPspCustomers>(sp => sp.GetRequiredService<EfPayments>())
            .AddScoped<ICardSetups>(sp => sp.GetRequiredService<EfPayments>())
            .AddScoped<IWebhookInbox>(sp => sp.GetRequiredService<EfPayments>())
            .AddScoped<ISettlements, EfSettlements>()
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

        using var message = JsonDocument.Parse(body);
        var root = message.RootElement;
        var command = new HandlePspWebhook(root.GetProperty("id").GetString()!, root.GetProperty("type").GetString()!, root.GetProperty("data").Clone());
        var dispatcher = services.GetRequiredService<IDispatcher>();
        var handled = await services.GetRequiredService<ModuleTransaction<PaymentsDbContext>>()
            .Run(c => dispatcher.Send<HandlePspWebhook, WebhookHandled>(command, c), ct);
        return Results.Ok(new { received = handled.Value.ToString().ToLowerInvariant() });
    }
}
