using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// One request that changed something, by the caller and the key the caller chose for it. Every
/// module keeps its own table, in its own schema, so the key commits with the module's change.
/// </summary>
public sealed class IdempotencyKey
{
    public Guid CallerId { get; set; }

    public required string Key { get; set; }

    public required byte[] RequestHash { get; set; }

    /// <summary>Empty only inside the transaction that inserted the row; a committed row always has its reply.</summary>
    public int? ReplyStatus { get; set; }

    public string? ReplyBody { get; set; }

    /// <summary>The <c>Location</c> of a <c>201 Created</c>, replayed with the body.</summary>
    public string? ReplyLocation { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class IdempotencyKeyModel
{
    public const string Table = "idempotency_keys";

    /// <summary>Maps the module's <c>idempotency_keys</c> table; call it from the module's DbContext.</summary>
    public static ModelBuilder MapIdempotencyKeys(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyKey>(key =>
        {
            key.ToTable(Table);
            key.HasKey(k => new { k.CallerId, k.Key });
            key.Property(k => k.Key).HasMaxLength(100);
            key.Property(k => k.ReplyBody).HasColumnType("json"); // json keeps the reply byte for byte; jsonb would reformat it
            key.Property(k => k.ReplyLocation).HasMaxLength(500);
            key.Property(k => k.CreatedAt).HasDefaultValueSql("now()");
        });
        return modelBuilder;
    }
}
