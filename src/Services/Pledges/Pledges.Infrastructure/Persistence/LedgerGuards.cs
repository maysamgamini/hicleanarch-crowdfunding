namespace Pledges.Infrastructure.Persistence;

/// <summary>One row per ledger entry: a replayed request or message cannot add a second entry. Pruned after 30 days.</summary>
internal sealed class LedgerCause
{
    public string CauseId { get; set; } = "";

    public string Kind { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One row per activated pledge: two different causes cannot activate one pledge twice. Never pruned.</summary>
internal sealed class PledgeActivation
{
    public Guid PledgeId { get; set; }
}
