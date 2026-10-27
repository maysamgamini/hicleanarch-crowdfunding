using BuildingBlocks.Domain;

namespace Payments.Domain;

public static class PaymentErrors
{
    public static readonly Error PspUnavailable = Error.Conflict("psp_unavailable", "The payment provider did not answer; try again in a moment.");
}
