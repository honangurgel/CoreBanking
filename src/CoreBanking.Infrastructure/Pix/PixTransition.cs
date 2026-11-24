namespace CoreBanking.Infrastructure.Pix;

public sealed class PixTransition
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
