namespace CoreBanking.Domain.Accounts;

public sealed class Account
{
    private Account()
    {
    }

    private Account(Guid id, Guid customerId, DateTimeOffset openedAt)
    {
        Id = id;
        CustomerId = customerId;
        OpenedAt = openedAt;
    }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public AccountStatus Status { get; private set; } = AccountStatus.Active;

    public static Account Open(Guid customerId)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("O identificador do cliente é obrigatório.", nameof(customerId));
        }

        return new Account(Guid.NewGuid(), customerId, DateTimeOffset.UtcNow);
    }
}
