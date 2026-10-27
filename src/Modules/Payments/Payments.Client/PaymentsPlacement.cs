using Microsoft.Extensions.Configuration;

namespace Payments.Client;

/// <summary>Where the Payments module runs: in this process, or in a service of its own.</summary>
public enum PaymentsMode
{
    InProcess,
    Remote,
}

/// <summary>How this process sends Payments its commands: through the dispatcher in memory, or over HTTP.</summary>
public enum PaymentsCalls
{
    InProcess,
    Http,
}

/// <summary>
/// Configuration section <c>Modules:Payments</c>, the switches of the split (lesson s4-a02's branch by
/// abstraction). <c>Mode = InProcess</c> (the default) runs the module here, with its endpoints, jobs,
/// handlers and queue; <c>Mode = Remote</c> runs none of them and sends its commands to
/// <c>BaseAddress</c>. <c>Calls = Http</c> with the module still here calls its own internal port over
/// loopback (s4-l03). Each switch back is the rollback.
/// </summary>
public sealed record PaymentsPlacement(PaymentsMode Mode, PaymentsCalls Calls, Uri BaseAddress)
{
    public const string Section = "Modules:Payments";

    /// <summary>Over loopback, this process's own internal port.</summary>
    public static readonly Uri Loopback = new("http://localhost:8081");

    public bool RunsHere => Mode == PaymentsMode.InProcess;

    public bool OverHttp => Calls == PaymentsCalls.Http;

    public static PaymentsPlacement From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var mode = section.GetValue("Mode", PaymentsMode.InProcess);
        var calls = section.GetValue("Calls", mode == PaymentsMode.Remote ? PaymentsCalls.Http : PaymentsCalls.InProcess);
        if (mode == PaymentsMode.Remote && calls != PaymentsCalls.Http)
        {
            throw new InvalidOperationException($"{Section}: Mode = Remote needs Calls = Http, because Payments does not run in this process.");
        }

        return new PaymentsPlacement(mode, calls, section.GetValue<Uri>("BaseAddress") ?? Loopback);
    }
}
