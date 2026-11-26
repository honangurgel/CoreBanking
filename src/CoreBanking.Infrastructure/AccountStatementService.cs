using CoreBanking.Application;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure;

public sealed class AccountStatementService(BankingDbContext db) : IAccountStatementService
{
    public async Task<AccountStatement?> FindAsync(Guid accountId, int pageSize, StatementCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        if (cursor?.EntryId == Guid.Empty)
            throw new ArgumentException("O identificador do cursor não pode ser vazio.", nameof(cursor));

        var ledgerId = await db.LedgerAccounts.AsNoTracking()
            .Where(account => account.CustomerAccountId == accountId)
            .Select(account => (Guid?)account.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (ledgerId is null)
        {
            if (await db.Accounts.AsNoTracking().AnyAsync(account => account.Id == accountId, cancellationToken))
                throw new InvalidOperationException($"A conta {accountId} não possui conta contábil.");
            return null;
        }

        var before = cursor?.CreatedAt.ToUniversalTime();
        var beforeId = cursor?.EntryId;
        var items = await db.Database.SqlQuery<StatementItem>($"""
            SELECT e."Id" AS "EntryId", e."CreatedAt", e."Description", p."AmountMinor", e."ReversesEntryId"
            FROM "LedgerPostings" p
            JOIN "LedgerEntries" e ON e."Id" = p."EntryId"
            WHERE p."LedgerAccountId" = {ledgerId.Value} AND e."Posted"
              AND (CAST({before} AS timestamptz) IS NULL
                   OR (e."CreatedAt", e."Id") < (CAST({before} AS timestamptz), CAST({beforeId} AS uuid)))
            ORDER BY e."CreatedAt" DESC, e."Id" DESC
            LIMIT {pageSize + 1}
            """).ToListAsync(cancellationToken);

        StatementCursor? next = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(pageSize);
            var last = items[^1];
            next = new StatementCursor(last.CreatedAt, last.EntryId);
        }
        return new AccountStatement(accountId, "BRL", items, next);
    }
}
