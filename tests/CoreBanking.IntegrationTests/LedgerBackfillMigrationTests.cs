using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace CoreBanking.IntegrationTests;

public sealed class LedgerBackfillMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync() => await postgres.StartAsync();

    public async Task DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task Migration_CreatesZeroBalanceForExistingAccounts()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        await using var db = new BankingDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261124164956_InitialCustomersAndAccounts");

        var customer = Customer.Create("Cliente fictício existente");
        var account = Account.Open(customer.Id);
        db.Customers.Add(customer);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();
        var ledger = await db.LedgerAccounts.AsNoTracking()
            .SingleAsync(item => item.CustomerAccountId == account.Id);
        Assert.Equal(0, ledger.BalanceMinor);

        await migrator.MigrateAsync();
        Assert.Equal(1, await db.LedgerAccounts.CountAsync(item => item.CustomerAccountId == account.Id));
    }

    [Fact]
    public async Task AccountCreation_RollsBackIfCustomerDoesNotExist()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        await using var db = new BankingDbContext(options);
        await db.Database.MigrateAsync();
        var account = Account.Open(Guid.NewGuid());

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new BankingRepository(db).AddAccountAsync(account, CancellationToken.None));

        await using var verification = new BankingDbContext(options);
        Assert.False(await verification.Accounts.AnyAsync(item => item.Id == account.Id));
        Assert.False(await verification.LedgerAccounts.AnyAsync(item => item.CustomerAccountId == account.Id));
    }
}
