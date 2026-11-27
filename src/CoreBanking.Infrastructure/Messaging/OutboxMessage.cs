namespace CoreBanking.Infrastructure.Messaging;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? TraceParent { get; set; }
}
