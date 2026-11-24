using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Worker;

public interface ITransferEventProcessor
{
    Task ProcessAsync(Guid messageId, Guid transferId, CancellationToken cancellationToken);
}

public sealed class TransferEventProcessor(BankingDbContext db) : ITransferEventProcessor
{
    public async Task ProcessAsync(Guid messageId, Guid transferId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InboxMessages" ("Id", "TransferId", "ReceivedAt")
            VALUES ({messageId}, {transferId}, {DateTimeOffset.UtcNow})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);
    }
}

public sealed class MessageProcessingException(string message, Exception? innerException = null)
    : Exception(message, innerException);
