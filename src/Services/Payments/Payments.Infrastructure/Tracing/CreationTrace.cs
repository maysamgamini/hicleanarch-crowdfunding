using System.Diagnostics;

using Microsoft.EntityFrameworkCore;

using Payments.Application;
using Payments.Infrastructure.Persistence;

namespace Payments.Infrastructure.Tracing;

/// <summary>
/// The PSP's webhook carries no trace of ours. Each card setup and charge stores the W3C context of the
/// work that created it, and the webhook about it starts a new trace that links to that context. A
/// new-card pledge is therefore two traces, the click to the card setup and the webhook to the
/// activation, joined by the link.
/// </summary>
internal static class CreationTrace
{
    public const string ActivitySourceName = "CrowdFunding.Payments";

    /// <summary>The column, on <c>card_setups</c> and <c>charges</c>, that holds the creating context.</summary>
    public const string Column = "TraceParent";

    /// <summary>The linked trace's id, for a backend that does not draw links.</summary>
    public const string LinkedTraceId = "crowdfunding.linked_trace_id";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>The context written with a new row: the span current while it is created.</summary>
    public static string? Current => Activity.Current?.Id;

    /// <summary>
    /// The context stored with the card setup or charge a PSP message is about, found by the PSP's
    /// object id before the message is handled; null when there is none.
    /// </summary>
    public static async Task<string?> Of(PaymentsDbContext db, HandlePspWebhook message, CancellationToken cancellationToken)
    {
        var data = message.Data;
        var query = message.Type switch
        {
            "setup_succeeded" or "setup_failed" => db.Database.SqlQuery<string?>($"""
                SELECT trace_parent AS "Value" FROM payments.card_setups WHERE psp_setup_id = {data.GetProperty("setup_id").GetString()}
                """),
            "charge_succeeded" or "charge_failed" when BuildingBlocks.Domain.PublicId.TryParse("plg", data.GetProperty("pledge_id").GetString(), out var pledgeId) =>
                db.Database.SqlQuery<string?>($"""
                SELECT trace_parent AS "Value" FROM payments.charges WHERE pledge_id = {pledgeId}
                """),
            "payment_succeeded" or "payment_failed" => db.Database.SqlQuery<string?>($"""
                SELECT c.trace_parent AS "Value"
                FROM payments.charge_attempts a JOIN payments.charges c ON c.pledge_id = a.pledge_id
                WHERE a.psp_payment_id = {data.GetProperty("payment_id").GetString()}
                """),
            _ => null,
        };
        return query is null ? null : (await query.ToListAsync(cancellationToken)).FirstOrDefault();
    }

    /// <summary>
    /// Starts the webhook's own trace, whatever headers the request came with: the PSP knows nothing of
    /// ours, and a context set by the network in between is not the story's. It links to the creating
    /// context and to the HTTP request's span. Disposing it ends the span and restores the request's.
    /// </summary>
    public static WebhookSpan StartWebhook(HandlePspWebhook message, string? creation)
    {
        var request = Activity.Current;
        var links = new List<ActivityLink>();
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("crowdfunding.psp.message_id", message.MessageId),
            new("crowdfunding.psp.message_type", message.Type),
        };
        if (creation is not null && ActivityContext.TryParse(creation, null, isRemote: true, out var created))
        {
            links.Add(new ActivityLink(created));
            tags.Add(new(LinkedTraceId, created.TraceId.ToString()));
        }

        if (request is not null)
        {
            links.Add(new ActivityLink(request.Context));
        }

        Activity.Current = null;
        var activity = Source.StartActivity($"psp webhook {message.Type}", ActivityKind.Consumer, default(ActivityContext), tags, links);
        if (activity is null)
        {
            Activity.Current = request;
        }

        return new WebhookSpan(activity, request);
    }
}

internal sealed class WebhookSpan(Activity? activity, Activity? request) : IDisposable
{
    public void Dispose()
    {
        activity?.Dispose();
        Activity.Current = request;
    }
}
