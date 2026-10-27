using CrowdFunding.RemoteCalls;

using Microsoft.Extensions.Configuration;

namespace Payments.Client;

/// <summary>
/// Configuration section <c>Services:Payments</c>: where Payments' service answers its commands
/// (<c>BaseAddress</c>, its internal port through Service Connect), and, in <c>Client</c>, the call's time
/// budget (<see cref="RemoteCallOptions"/>: 2 s an attempt, 5 s in all, by default) and its bulkhead
/// (<c>Client:MaxConcurrentCalls</c>, s5-l12: card setups one task keeps in flight). Since lesson s5-l10 no
/// <c>Modules:*</c> setting exists any more: the platform refuses to start a program that still sets one.
/// </summary>
public sealed record PaymentsPlacement(Uri BaseAddress, RemoteCallOptions Client)
{
    public const string Section = "Services:Payments";

    /// <summary>Payments' internal port through Service Connect (lesson s4-l05).</summary>
    public static readonly Uri ServiceConnect = new("http://payments:8081");

    /// <summary>
    /// An estimate to be replaced by a measurement: a launch's 200 new-card pledges a second per task at a
    /// healthy Payments' 150 ms is 30 in flight (Little's law); 40 leaves room for a burst. At 1.9 s it
    /// would be 380, which the limit refuses.
    /// </summary>
    public const int DefaultMaxConcurrentCalls = 40;

    public static PaymentsPlacement From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var client = section.GetSection("Client").Get<RemoteCallOptions>() ?? new RemoteCallOptions();
        return new PaymentsPlacement(section.GetValue<Uri>("BaseAddress") ?? ServiceConnect,
            client with { MaxConcurrentCalls = client.MaxConcurrentCalls ?? DefaultMaxConcurrentCalls });
    }
}
