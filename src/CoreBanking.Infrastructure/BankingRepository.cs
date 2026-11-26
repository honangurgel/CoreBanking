using CoreBanking.Application;
using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using CoreBanking.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure;

public sealed class BankingRepository(BankingDbContext db) : IBankingRepository
{
    public Task<Customer?> FindCustomerAsync(Guid id, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken);

    public async Task<AccountDetails?> FindAccountAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await (
            from account in db.Accounts.AsNoTracking()
            join ledger in db.LedgerAccounts.AsNoTracking() on account.Id equals ledger.CustomerAccountId into ledgerAccounts
            from ledger in ledgerAccounts.DefaultIfEmpty()
            where account.Id == id
            select new { Account = account, BalanceMinor = ledger == null ? (long?)null : ledger.BalanceMinor }
        ).SingleOrDefaultAsync(cancellationToken);

        if (result is null)
        {
            return null;
        }

        return new AccountDetails(result.Account, result.BalanceMinor
            ?? throw new InvalidOperationException($"A conta {id} não possui conta contábil."));
    }

    public async Task AddCustomerAsync(Customer customer, CancellationToken cancellationToken)
    {
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAccountAsync(Account account, AuditActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Accounts.Add(account);
        db.LedgerAccounts.Add(LedgerAccount.ForCustomer(account.Id));
        await db.SaveChangesAsync(cancellationToken);
        if (actor.Kind == "Oidc")
            await AccountAccessService.ChangeWithinTransactionAsync(db, actor, account.Id, actor.Issuer!, actor.Subject!, true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
