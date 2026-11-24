using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using CoreBanking.Domain.Ledger;
using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoreBanking.IntegrationTests;

public sealed class LedgerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await postgres.DisposeAsync();

    private BankingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private async Task<(Guid CustomerLedgerId, Guid SystemLedgerId)> CreateLedgerAccountsAsync()
    {
        await using var db = CreateContext();
        var customer = Customer.Create("Cliente fictício");
        var account = Account.Open(customer.Id);
        db.Customers.Add(customer);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var customerLedger = LedgerAccount.ForCustomer(account.Id);
        var systemLedger = LedgerAccount.ForSystem();
        var writer = new LedgerWriter(db);
        await writer.CreateAccountAsync(customerLedger);
        await writer.CreateAccountAsync(systemLedger);
        return (customerLedger.Id, systemLedger.Id);
    }

    [Fact]
    public async Task PostingAndReversal_UpdateBalancesAndPreventMutation()
    {
        var (customerId, systemId) = await CreateLedgerAccountsAsync();
        var entry = LedgerEntry.Create("Depósito fictício", [new(customerId, 100), new(systemId, -100)]);
        await using var db = CreateContext();
        var writer = new LedgerWriter(db);
        await writer.PostAsync(entry);

        var balances = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(account => account.Id, account => account.BalanceMinor);
        Assert.Equal(100, balances[customerId]);
        Assert.Equal(-100, balances[systemId]);

        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"LedgerPostings\" SET \"AmountMinor\" = 1 WHERE \"EntryId\" = {entry.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"LedgerEntries\" WHERE \"Id\" = {entry.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"LedgerAccounts\" SET \"BalanceMinor\" = 1000 WHERE \"Id\" = {customerId}"));
        var thirdAccount = LedgerAccount.ForSystem();
        await writer.CreateAccountAsync(thirdAccount);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"LedgerPostings\" (\"EntryId\", \"LedgerAccountId\", \"AmountMinor\") VALUES ({entry.Id}, {thirdAccount.Id}, {1L})"));

        await writer.PostAsync(entry.CreateReversal("Estorno fictício"));
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => writer.PostAsync(entry.CreateReversal("Estorno duplicado")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        balances = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(account => account.Id, account => account.BalanceMinor);
        Assert.Equal(0, balances[customerId]);
        Assert.Equal(0, balances[systemId]);
        var entryCount = await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"LedgerEntries\"").SingleAsync();
        Assert.Equal(2, entryCount);
    }

    [Fact]
    public async Task Database_RejectsUnbalancedAndUnfinishedEntries()
    {
        var (customerId, systemId) = await CreateLedgerAccountsAsync();
        await using var db = CreateContext();
        var id = Guid.NewGuid();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"LedgerEntries\" (\"Id\", \"Description\", \"CreatedAt\", \"Posted\") VALUES ({id}, {"Teste"}, {DateTimeOffset.UtcNow}, false)");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"LedgerPostings\" (\"EntryId\", \"LedgerAccountId\", \"AmountMinor\") VALUES ({id}, {customerId}, {100L}), ({id}, {systemId}, {-99L})");
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"LedgerEntries\" SET \"Posted\" = true WHERE \"Id\" = {id}"));
            await transaction.RollbackAsync();
        }

        var unfinished = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"LedgerEntries\" (\"Id\", \"Description\", \"CreatedAt\", \"Posted\") VALUES ({unfinished}, {"Teste"}, {DateTimeOffset.UtcNow}, false)");
            await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        }

        var balances = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(account => account.Id, account => account.BalanceMinor);
        Assert.Equal(0, balances[customerId]);
        Assert.Equal(0, balances[systemId]);
    }

    [Fact]
    public async Task ConcurrentWithdrawals_CannotOverdrawCustomerAccount()
    {
        var (customerId, systemId) = await CreateLedgerAccountsAsync();
        await using (var db = CreateContext())
        {
            await new LedgerWriter(db).PostAsync(LedgerEntry.Create("Depósito",
                [new(customerId, 100), new(systemId, -100)]));
        }

        async Task<bool> WithdrawAsync()
        {
            await using var db = CreateContext();
            try
            {
                await new LedgerWriter(db).PostAsync(LedgerEntry.Create("Saque",
                    [new(customerId, -80), new(systemId, 80)]));
                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(WithdrawAsync(), WithdrawAsync());
        Assert.Single(results, success => success);

        await using var verification = CreateContext();
        var balances = await verification.LedgerAccounts.AsNoTracking().ToDictionaryAsync(account => account.Id, account => account.BalanceMinor);
        Assert.Equal(20, balances[customerId]);
        Assert.Equal(-20, balances[systemId]);
        var entryCount = await verification.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"LedgerEntries\"").SingleAsync();
        Assert.Equal(2, entryCount);
        var differences = await verification.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM (
                SELECT a."Id" FROM "LedgerAccounts" a
                LEFT JOIN "LedgerPostings" p ON p."LedgerAccountId" = a."Id"
                GROUP BY a."Id", a."BalanceMinor"
                HAVING a."BalanceMinor" <> coalesce(sum(p."AmountMinor"), 0)
            ) discrepancies
            """).SingleAsync();
        Assert.Equal(0, differences);
    }
}
