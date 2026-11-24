namespace CoreBanking.Infrastructure.Messaging;

public sealed class InboxMessage
{
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
