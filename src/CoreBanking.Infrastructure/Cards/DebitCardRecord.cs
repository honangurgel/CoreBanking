namespace CoreBanking.Infrastructure.Cards;

public sealed class DebitCardRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DebitAuthorization
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public Guid AccountId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public long AmountMinor { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public Guid? ReservationEntryId { get; set; }
    public Guid? FinalizationEntryId { get; set; }
}
