using CoreBanking.Application;
using CoreBanking.Application.Deposits;
using CoreBanking.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure.Deposits;

public sealed class DepositService(BankingDbContext db) : IDepositService
{
    private static readonly Guid CounterpartyId = Guid.Parse("a6127328-00c1-48fd-8c22-fd22289349fa");

    public async Task<DepositResult?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var deposit = await db.Deposits.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return deposit is null ? null : ToResult(deposit);
    }

    public async Task<DepositResponse> CreateAsync(Guid accountId, long amountMinor, string key, AuditActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var id = Guid.NewGuid();
        var clock = DateTimeOffset.UtcNow;
        var now = clock.AddTicks(-(clock.Ticks % 10));
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Deposits" ("Id", "IdempotencyKey", "AccountId", "AmountMinor", "CreatedAt")
            VALUES ({id}, {key}, {accountId}, {amountMinor}, {now})
            ON CONFLICT ("IdempotencyKey") DO NOTHING
            """, cancellationToken);
        if (inserted == 0)
        {
            var existing = await db.Deposits.AsNoTracking().SingleAsync(item => item.IdempotencyKey == key, cancellationToken);
            if (existing.AccountId != accountId || existing.AmountMinor != amountMinor)
            {
                return new DepositResponse(DepositOutcome.IdempotencyConflict, null);
            }

            if (existing.LedgerEntryId is null)
            {
                throw new InvalidOperationException($"Depósito {existing.Id} não possui lançamento.");
            }
            return new DepositResponse(DepositOutcome.Completed, ToResult(existing));
        }

        var ledger = await db.LedgerAccounts.AsNoTracking()
            .SingleOrDefaultAsync(account => account.CustomerAccountId == accountId, cancellationToken);
        if (ledger is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DepositResponse(DepositOutcome.AccountNotFound, null);
        }

        var entry = LedgerEntry.Create("Depósito fictício",
            [new LedgerPosting(ledger.Id, amountMinor), new LedgerPosting(CounterpartyId, -amountMinor)]);
        await new LedgerWriter(db).PostWithinTransactionAsync(entry, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Deposits\" SET \"LedgerEntryId\" = {entry.Id} WHERE \"Id\" = {id}", cancellationToken);
        await OperationAuditWriter.WriteAsync(db, actor, "Deposit", "Completed", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DepositResponse(DepositOutcome.Completed, new DepositResult(id, accountId, amountMinor, now));
    }

    private static DepositResult ToResult(DepositRecord deposit) =>
        new(deposit.Id, deposit.AccountId, deposit.AmountMinor, deposit.CreatedAt);
}
