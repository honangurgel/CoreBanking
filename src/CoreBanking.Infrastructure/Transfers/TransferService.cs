using CoreBanking.Application;
using CoreBanking.Application.Transfers;
using CoreBanking.Domain.Ledger;
using CoreBanking.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure.Transfers;

public sealed class TransferService(BankingDbContext db) : ITransferService
{
    public async Task<TransferResult?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var transfer = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return transfer is null ? null : ToResult(transfer);
    }

    public async Task<TransferResponse> CreateAsync(Guid sourceAccountId, Guid destinationAccountId, long amountMinor, string key, AuditActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var id = Guid.NewGuid();
        var clock = DateTimeOffset.UtcNow;
        var now = clock.AddTicks(-(clock.Ticks % 10));
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Transfers" ("Id", "IdempotencyKey", "SourceAccountId", "DestinationAccountId", "AmountMinor", "Status", "CreatedAt")
            VALUES ({id}, {key}, {sourceAccountId}, {destinationAccountId}, {amountMinor}, {"Pending"}, {now})
            ON CONFLICT ("IdempotencyKey") DO NOTHING
            """, cancellationToken);

        if (inserted == 0)
        {
            var existing = await db.Transfers.AsNoTracking().SingleAsync(item => item.IdempotencyKey == key, cancellationToken);
            if (existing.SourceAccountId != sourceAccountId || existing.DestinationAccountId != destinationAccountId || existing.AmountMinor != amountMinor)
            {
                return new TransferResponse(TransferOutcome.IdempotencyConflict, null);
            }

            var outcome = existing.Status switch
            {
                "Completed" => TransferOutcome.Completed,
                "InsufficientFunds" => TransferOutcome.InsufficientFunds,
                _ => throw new InvalidOperationException($"Estado inesperado da transferência {existing.Id}: {existing.Status}.")
            };
            return new TransferResponse(outcome, ToResult(existing));
        }

        var accounts = await db.LedgerAccounts.FromSqlInterpolated($"""
            SELECT * FROM "LedgerAccounts"
            WHERE "CustomerAccountId" IN ({sourceAccountId}, {destinationAccountId})
            ORDER BY "Id" FOR UPDATE
            """).AsNoTracking().ToListAsync(cancellationToken);

        if (accounts.Count != 2)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new TransferResponse(TransferOutcome.AccountNotFound, null);
        }

        var source = accounts.Single(account => account.CustomerAccountId == sourceAccountId);
        var destination = accounts.Single(account => account.CustomerAccountId == destinationAccountId);
        if (source.BalanceMinor < amountMinor)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Transfers\" SET \"Status\" = {"InsufficientFunds"} WHERE \"Id\" = {id}", cancellationToken);
            await OperationAuditWriter.WriteAsync(db, actor, "Transfer", "InsufficientFunds", id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TransferResponse(TransferOutcome.InsufficientFunds, new TransferResult(id, sourceAccountId, destinationAccountId, amountMinor, "InsufficientFunds", now));
        }

        var entry = LedgerEntry.Create("Transferência interna fictícia",
            [new LedgerPosting(source.Id, -amountMinor), new LedgerPosting(destination.Id, amountMinor)]);
        await new LedgerWriter(db).PostWithinTransactionAsync(entry, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Transfers\" SET \"Status\" = {"Completed"}, \"LedgerEntryId\" = {entry.Id} WHERE \"Id\" = {id}", cancellationToken);
        db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), TransferId = id, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await OperationAuditWriter.WriteAsync(db, actor, "Transfer", "Completed", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TransferResponse(TransferOutcome.Completed, new TransferResult(id, sourceAccountId, destinationAccountId, amountMinor, "Completed", now));
    }

    private static TransferResult ToResult(TransferRecord transfer) =>
        new(transfer.Id, transfer.SourceAccountId, transfer.DestinationAccountId, transfer.AmountMinor, transfer.Status, transfer.CreatedAt);
}
