using BuildingBlocks.Infrastructure.Identity;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Payments.Contracts;

namespace Payments.Infrastructure;

/// <summary>
/// Who may call Payments' internal endpoints: another service, with a token from Cognito's resource
/// server <c>payments</c>. One scope, for commands; a read scope is added when a read has a caller.
/// </summary>
internal static class PaymentsCallers
{
    public const string Commands = PaymentsInternalEndpoints.CommandsScope;

    public static IServiceCollection AddPaymentsCallers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddServiceTokenAuthentication(configuration);
        services.AddAuthorizationBuilder().AddScopePolicy(Commands);
        return services;
    }

    /// <summary>Only a listed service holding <see cref="Commands"/>: no user, whatever their groups.</summary>
    public static TBuilder RequirePaymentsCommands<TBuilder>(this TBuilder endpoint) where TBuilder : IEndpointConventionBuilder =>
        endpoint.RequireAuthorization(Commands);
}
