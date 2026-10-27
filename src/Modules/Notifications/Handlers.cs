using System.Globalization;

using BuildingBlocks.Application;

using Campaigns.Contracts;

using Payments.Contracts;

using Pledges.Contracts;

namespace Notifications;

/// <summary>
/// Notifications reacts and publishes nothing. It has no rules of its own beyond "tell people once",
/// so it is one project: the handlers, the storage and the senders together.
/// </summary>
internal sealed class Handlers(Notifier notifier) :
    IIntegrationEventHandler<CampaignPublished>,
    IIntegrationEventHandler<CampaignSettled>,
    IIntegrationEventHandler<PledgeActivated>,
    IIntegrationEventHandler<PledgeCancelled>,
    IIntegrationEventHandler<CampaignFundingDecided>,
    IIntegrationEventHandler<CampaignCancelled>,
    IIntegrationEventHandler<PledgeReleased>,
    IIntegrationEventHandler<PledgeChargeSucceeded>,
    IIntegrationEventHandler<PledgeChargeFailed>
{
    public Task Handle(CampaignPublished e, CancellationToken ct) => notifier.RememberCreator(e.CampaignId, e.CreatorId, ct);

    /// <summary>The creator's summary, once every charge has ended.</summary>
    public async Task Handle(CampaignSettled e, CancellationToken ct)
    {
        if (await notifier.CreatorOf(e.CampaignId, ct) is { } creator)
        {
            await notifier.Tell(e.EventId, creator, "campaign_settled", "Your campaign is settled",
                $"{e.Charged.ToString(CultureInfo.InvariantCulture)} pledges were charged; {e.Failed.ToString(CultureInfo.InvariantCulture)} could not be charged and were dropped.", ct);
        }
    }

    public async Task Handle(PledgeActivated e, CancellationToken ct)
    {
        await notifier.Remember(e.CampaignId, e.BackerId, ct);
        await notifier.Tell(e.EventId, e.BackerId, "pledge_confirmed", "You're a backer",
            $"Your pledge of {Money(e.AmountMinor, e.Currency)} counts. You will be charged only if the campaign is funded, at its deadline.", ct);
    }

    public Task Handle(PledgeCancelled e, CancellationToken ct) => notifier.Forget(e.CampaignId, e.BackerId, ct);

    public async Task Handle(CampaignFundingDecided e, CancellationToken ct)
    {
        var funded = e.Outcome == CampaignFundingDecided.Funded;
        foreach (var backer in await notifier.BackersOf(e.CampaignId, ct))
        {
            await notifier.Tell(e.EventId, backer, funded ? "campaign_funded" : "campaign_unsuccessful",
                funded ? "The campaign is funded" : "The campaign was not funded",
                funded ? "It reached its goal. Your saved card will be charged in the next few minutes." : "It did not reach its goal. You were not charged, and you will not be.", ct);
        }
    }

    /// <summary>The creator only: each backer is told by their own pledge's <see cref="PledgeReleased"/>.</summary>
    public Task Handle(CampaignCancelled e, CancellationToken ct) =>
        notifier.Tell(e.EventId, e.CreatorId, "campaign_cancelled", "Your campaign is cancelled", "Nobody was charged.", ct);

    /// <summary>
    /// One per pledge of a cancelled campaign, published with its release, so a pledge that committed
    /// just after the cancel is told too, whatever order the events arrive in.
    /// </summary>
    public async Task Handle(PledgeReleased e, CancellationToken ct)
    {
        await notifier.Forget(e.CampaignId, e.BackerId, ct);
        await notifier.Tell(e.EventId, e.BackerId, "campaign_cancelled", "A campaign you back was cancelled",
            "Its creator cancelled it, so your pledge was released. You were not charged, and you will not be.", ct);
    }

    public Task Handle(PledgeChargeSucceeded e, CancellationToken ct) =>
        notifier.Tell(e.EventId, e.BackerId, "charge_succeeded", "You were charged", $"Your card was charged {e.AmountMinor.ToString(CultureInfo.InvariantCulture)} (minor units).", ct);

    // Lesson s3-a04: every decline in the recovery window asks the backer to fix the payment; the last one says it was dropped.
    public Task Handle(PledgeChargeFailed e, CancellationToken ct) =>
        e.Final
            ? notifier.Tell(e.EventId, e.BackerId, "charge_dropped", "We could not charge your card", "Your card was declined for 7 days, so your pledge was dropped. The campaign goes ahead without it.", ct)
            : notifier.Tell(e.EventId, e.BackerId, "charge_declined", "Your card was declined",
                e.DeclineCode == "authentication_required"
                    ? "Your bank wants you to confirm this payment. Open your pledge to confirm it within 7 days."
                    : e.RetriedOnItsOwn
                        ? "We will try again. You can also give another card from your pledge within 7 days."
                        : "Your bank will not accept this card, so we will not try it again. Give another card from your pledge within 7 days, or the pledge is dropped.", ct);

    private static string Money(long minor, string currency) =>
        $"{(minor / 100m).ToString("0.00", CultureInfo.InvariantCulture)} {currency}";
}
