using BuildingBlocks.Domain;

namespace Payments.Domain;

public static class PaymentErrors
{
    public static readonly Error PspUnavailable = Error.Conflict("psp_unavailable", "The payment provider did not answer; try again in a moment.");

    /// <summary>The card-setup lane is full: the pledge and its hold are kept, and the card form opens when its turn comes.</summary>
    public static Error CardSetupLaneFull(TimeSpan retryAfter) =>
        Error.TooManyRequests("card_setup_lane_full", "Many backers are saving cards right now; your place is held. Ask again shortly.", retryAfter);
}
