using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;

namespace CoreBanking.UnitTests;

public class CustomerAccountTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Customer_RejectsEmptyNames(string? name)
    {
        Assert.Throws<ArgumentException>(() => Customer.Create(name));
    }

    [Fact]
    public void Customer_RejectsNameLongerThanLimit()
    {
        Assert.Throws<ArgumentException>(() => Customer.Create(new string('a', 201)));
    }

    [Fact]
    public void Customer_TrimsName()
    {
        var customer = Customer.Create("  Cliente Fictício  ");
        Assert.Equal("Cliente Fictício", customer.Name);
        Assert.NotEqual(Guid.Empty, customer.Id);
    }

    [Fact]
    public void Account_RequiresCustomer()
    {
        Assert.Throws<ArgumentException>(() => Account.Open(Guid.Empty));
    }

    [Fact]
    public void Account_StartsActiveForCustomer()
    {
        var customerId = Guid.NewGuid();
        var account = Account.Open(customerId);

        Assert.Equal(customerId, account.CustomerId);
        Assert.Equal(AccountStatus.Active, account.Status);
    }
}
