using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;

namespace Payments.Application;

public static class PaymentsApplication
{
    public static IServiceCollection AddPaymentsApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<StartCardSetup, CardSetupStarted>, StartCardSetupHandler>()
            .AddScoped<ICommandHandler<HandlePspWebhook, WebhookHandled>, HandlePspWebhookHandler>()
            .AddScoped<ICommandHandler<RunSettlement, bool>, RunSettlementHandler>()
            .AddScoped<IQueryHandler<GetSettlementProgress, SettlementProgress>, GetSettlementProgressHandler>()
            .AddScoped<ICommandHandler<UseNewCard, ChargeView>, UseNewCardHandler>()
            .AddScoped<ICommandHandler<ConfirmPayment, PaymentToConfirm>, ConfirmPaymentHandler>()
            .AddScoped<SettlementStep>()
            .AddScoped<ChargeOutcomes>()
            .AddScoped<ChargeBookkeeping>()
            .AddScoped<ICommandHandler<PayOut, PayoutDone>, PayOutHandler>()
            .AddScoped<ICommandHandler<RefundPledge, RefundDone>, RefundPledgeHandler>()
            .AddScoped<ICommandHandler<RunMoneyPass, MoneyPass>, RunMoneyPassHandler>()
            .AddScoped<IQueryHandler<GetCampaignMoney, CampaignMoney>, GetCampaignMoneyHandler>();
}
