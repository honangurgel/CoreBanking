namespace CoreBanking.Infrastructure.Transfers;

public sealed class TransferRecord
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public Guid SourceAccountId { get; set; }
    public Guid DestinationAccountId { get; set; }
    public long AmountMinor { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? LedgerEntryId { get; set; }
}
