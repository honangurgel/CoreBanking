using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;

namespace CoreBanking.Application;

public interface IBankingRepository
{
    Task<Customer?> FindCustomerAsync(Guid id, CancellationToken cancellationToken);
    Task<Account?> FindAccountAsync(Guid id, CancellationToken cancellationToken);
    Task AddCustomerAsync(Customer customer, CancellationToken cancellationToken);
    Task AddAccountAsync(Account account, CancellationToken cancellationToken);
}
