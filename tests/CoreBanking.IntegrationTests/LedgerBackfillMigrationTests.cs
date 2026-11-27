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
            new BankingRepository(db).AddAccountAsync(account, CoreBanking.Application.AuditActor.Local("migration-test"), CancellationToken.None));

        await using var verification = new BankingDbContext(options);
        Assert.False(await verification.Accounts.AnyAsync(item => item.Id == account.Id));
        Assert.False(await verification.LedgerAccounts.AnyAsync(item => item.CustomerAccountId == account.Id));
    }

    [Fact]
    public async Task TraceMigration_PreservesExistingOperationsAndMessages()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new BankingDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261126150000_ScopeIdempotencyByAccount");
        var customer = Customer.Create("Cliente fictício existente");
        var account = Account.Open(customer.Id);
        db.Customers.Add(customer);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var transferId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var pixId = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var key = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Transfers" ("Id", "IdempotencyKey", "SourceAccountId", "DestinationAccountId", "AmountMinor", "Status", "CreatedAt")
            VALUES ({transferId}, {"legacy-transfer"}, {account.Id}, {destination}, {1L}, {"Completed"}, now());
            INSERT INTO "OutboxMessages" ("Id", "TransferId", "CreatedAt") VALUES ({messageId}, {transferId}, now());
            INSERT INTO "PixOperations" ("Id", "IdempotencyKey", "AccountId", "KeyId", "InstitutionIspb", "Direction",
                "AmountMinor", "Scenario", "Status", "CreatedAt", "Attempts", "NextAttemptAt")
            VALUES ({pixId}, {"legacy-pix"}, {account.Id}, {key}, {"00000000"}, {"Incoming"},
                {1L}, {"Confirm"}, {"Pending"}, now(), {0}, now());
            """);
        await migrator.MigrateAsync();
        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync(item => item.Id == messageId);
        var pix = await db.PixOperations.AsNoTracking().SingleAsync(item => item.Id == pixId);
        Assert.Equal(transferId, outbox.TransferId);
        Assert.Null(outbox.TraceParent);
        Assert.Equal("Pending", pix.Status);
        Assert.Null(pix.TraceParent);
    }
}
