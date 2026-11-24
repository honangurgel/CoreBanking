namespace CoreBanking.Domain.Ledger;

public sealed class LedgerAccount
{
    private LedgerAccount()
    {
    }

    private LedgerAccount(Guid? customerAccountId)
    {
        Id = Guid.NewGuid();
        CustomerAccountId = customerAccountId;
    }

    public Guid Id { get; private set; }
    public Guid? CustomerAccountId { get; private set; }
    public long BalanceMinor { get; private set; }

    public static LedgerAccount ForCustomer(Guid accountId)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("A conta de cliente é obrigatória.", nameof(accountId));
        }

        return new LedgerAccount(accountId);
    }

    public static LedgerAccount ForSystem() => new(null);
}
