using CoreBanking.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure;

public sealed class LedgerWriter(BankingDbContext db)
{
    public async Task CreateAccountAsync(LedgerAccount account, CancellationToken cancellationToken = default)
    {
        db.LedgerAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PostAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PostWithinTransactionAsync(entry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PostWithinTransactionAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Uma transação ativa é necessária para registrar o lançamento.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"LedgerEntries\" (\"Id\", \"Description\", \"CreatedAt\", \"Posted\", \"ReversesEntryId\") VALUES ({entry.Id}, {entry.Description}, {entry.CreatedAt}, false, {entry.ReversesEntryId})",
            cancellationToken);

        foreach (var posting in entry.Postings)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"LedgerPostings\" (\"EntryId\", \"LedgerAccountId\", \"AmountMinor\") VALUES ({entry.Id}, {posting.LedgerAccountId}, {posting.AmountMinor})",
                cancellationToken);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"LedgerEntries\" SET \"Posted\" = true WHERE \"Id\" = {entry.Id}",
            cancellationToken);
    }
}
