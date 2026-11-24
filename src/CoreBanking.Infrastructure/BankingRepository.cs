using CoreBanking.Application;
using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure;

public sealed class BankingRepository(BankingDbContext db) : IBankingRepository
{
    public Task<Customer?> FindCustomerAsync(Guid id, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken);

    public Task<Account?> FindAccountAsync(Guid id, CancellationToken cancellationToken) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == id, cancellationToken);

    public async Task AddCustomerAsync(Customer customer, CancellationToken cancellationToken)
    {
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAccountAsync(Account account, CancellationToken cancellationToken)
    {
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
    }
}
