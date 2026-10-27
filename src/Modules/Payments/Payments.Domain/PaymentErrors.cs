using BuildingBlocks.Domain;

namespace Payments.Domain;

public static class PaymentErrors
{
    public static readonly Error PspUnavailable = Error.Conflict("psp_unavailable", "The payment provider did not answer; try again in a moment.");

    public static readonly Error ChargeNotFound = Error.NotFound("charge_not_found", "You have no charge for this pledge.");
    public static readonly Error NotAwaitingBacker = Error.Conflict("charge_not_awaiting_backer", "This charge is not waiting for you right now.");
    public static readonly Error NeedsConfirmationNotCard = Error.Conflict("charge_needs_confirmation", "The bank wants you to confirm this payment; a new card is not needed.");
    public static readonly Error PaymentMethodRequired = Error.Validation("payment_method_required", "Send the saved card to charge (payment_method_id).");
    public static readonly Error NoSettlement = Error.NotFound("settlement_not_found", "This campaign has no settlement.");

    /// <summary>The card-setup lane is full: the pledge and its hold are kept, and the card form opens when its turn comes.</summary>
    public static Error CardSetupLaneFull(TimeSpan retryAfter) =>
        Error.TooManyRequests("card_setup_lane_full", "Many backers are saving cards right now; your place is held. Ask again shortly.", retryAfter);

    public static readonly Error JournalTooShort = Error.Validation("journal_too_short", "A money journal needs at least two lines that are not zero.");
    public static readonly Error JournalUnbalanced = Error.Validation("journal_unbalanced", "A money journal's lines must sum to zero.");
    public static readonly Error NotSettled = Error.Conflict("not_settled", "The campaign's settlement has not finished.");
    public static readonly Error NotPayable = Error.Conflict("not_payable", "The campaign's money cannot be paid out yet.");
    public static readonly Error ChargeNotBooked = Error.Conflict("charge_not_booked", "A charge has no journal and the payment provider does not confirm it; it must be looked into before payout.");
    public static readonly Error ChargeNotRefundable = Error.Conflict("charge_not_refundable", "Only a charged pledge can be refunded.");
}
