namespace CoreBanking.Infrastructure.Deposits;

public sealed class DepositRecord
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public Guid AccountId { get; set; }
    public long AmountMinor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? LedgerEntryId { get; set; }
}
