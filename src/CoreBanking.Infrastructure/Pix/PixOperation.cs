namespace CoreBanking.Infrastructure.Pix;

public sealed class PixOperation
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public Guid AccountId { get; set; }
    public Guid KeyId { get; set; }
    public string InstitutionIspb { get; set; } = null!;
    public string Direction { get; set; } = null!;
    public long AmountMinor { get; set; }
    public string Scenario { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ReservationEntryId { get; set; }
    public Guid? SettlementEntryId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? TraceParent { get; set; }
}
