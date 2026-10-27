using CrowdFunding.RemoteCalls;

using Microsoft.Extensions.Configuration;

namespace Payments.Client;

/// <summary>
/// Configuration section <c>Services:Payments</c>: where Payments' service answers its commands
/// (<c>BaseAddress</c>, its internal port through Service Connect), and, in <c>Client</c>, the call's time
/// budget (<see cref="RemoteCallOptions"/>: 2 s an attempt, 5 s in all, by default). Since lesson s5-l10 no
/// <c>Modules:*</c> setting exists any more: the platform refuses to start a program that still sets one.
/// </summary>
public sealed record PaymentsPlacement(Uri BaseAddress, RemoteCallOptions Client)
{
    public const string Section = "Services:Payments";

    /// <summary>Payments' internal port through Service Connect (lesson s4-l05).</summary>
    public static readonly Uri ServiceConnect = new("http://payments:8081");

    public static PaymentsPlacement From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        return new PaymentsPlacement(section.GetValue<Uri>("BaseAddress") ?? ServiceConnect,
            section.GetSection("Client").Get<RemoteCallOptions>() ?? new RemoteCallOptions());
    }
}
