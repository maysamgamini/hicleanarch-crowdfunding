using CrowdFunding.RemoteCalls;

using Microsoft.Extensions.Configuration;

namespace Payments.Client;

/// <summary>
/// Configuration section <c>Modules:Payments</c>: where Payments' service answers its commands
/// (<c>BaseAddress</c>, its internal port through Service Connect), and, in <c>Client</c>, the call's time
/// budget (<see cref="RemoteCallOptions"/>: 2 s an attempt, 5 s in all, by default). Payments runs only in its own service
/// since lesson s4-l10, so the switches of the split (lesson s4-a02's branch by abstraction), <c>Mode</c>
/// and <c>Calls</c>, are gone: a program that still sets one refuses to start rather than ignore it.
/// </summary>
public sealed record PaymentsPlacement(Uri BaseAddress, RemoteCallOptions Client)
{
    public const string Section = "Modules:Payments";

    /// <summary>Payments' internal port through Service Connect (lesson s4-l05).</summary>
    public static readonly Uri ServiceConnect = new("http://payments:8081");

    /// <summary>The switches of s4-l03 and s4-l05, removed here.</summary>
    private static readonly string[] Removed = ["Mode", "Calls"];

    public static PaymentsPlacement From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var removed = Removed.Where(key => section[key] is not null).ToList();
        if (removed.Count > 0)
        {
            throw new InvalidOperationException(
                $"{Section}:{string.Join(" and ", removed)} no longer exist (lesson s4-l10): Payments runs only in its own service, " +
                $"and this program sends it its commands over HTTP. Remove the setting.");
        }

        return new PaymentsPlacement(section.GetValue<Uri>("BaseAddress") ?? ServiceConnect,
            section.GetSection("Client").Get<RemoteCallOptions>() ?? new RemoteCallOptions());
    }
}
