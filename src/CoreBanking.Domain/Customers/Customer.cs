namespace CoreBanking.Domain.Customers;

public sealed class Customer
{
    private Customer()
    {
    }

    private Customer(Guid id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static Customer Create(string? name)
    {
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
        {
            throw new ArgumentException("O nome deve conter entre 1 e 200 caracteres.", nameof(name));
        }

        return new Customer(Guid.NewGuid(), normalizedName, DateTimeOffset.UtcNow);
    }
}
