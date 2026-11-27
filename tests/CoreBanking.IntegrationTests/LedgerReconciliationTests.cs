using CoreBanking.Domain.Ledger;
using CoreBanking.Infrastructure;
using CoreBanking.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoreBanking.IntegrationTests;

public sealed class LedgerReconciliationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await postgres.DisposeAsync();

    private BankingDbContext CreateContext() => new(new DbContextOptionsBuilder<BankingDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);

    [Fact]
    public async Task Reconciliation_ReportsCompletedBatches_AndRestartsAfterDatabaseFailure()
    {
        await using var db = CreateContext();
        var first = LedgerAccount.ForSystem();
        var second = LedgerAccount.ForSystem();
        var writer = new LedgerWriter(db);
        await writer.CreateAccountAsync(first);
        await writer.CreateAccountAsync(second);
        await writer.PostAsync(LedgerEntry.Create("Teste fictício", [new(first.Id, 100), new(second.Id, -100)]));
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "LedgerAccounts" ("Id", "CustomerAccountId", "BalanceMinor")
            SELECT gen_random_uuid(), NULL, 0 FROM generate_series(1, 105)
            """);
        var total = await db.LedgerAccounts.CountAsync();
        using var services = new ServiceCollection()
            .AddDbContext<BankingDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()))
            .BuildServiceProvider();
        using var recorder = new WorkerMetricRecorder();
        using var monitor = new LedgerReconciliationMonitor(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LedgerReconciliationMonitor>.Instance);

        await monitor.SampleAsync(CancellationToken.None);
        Assert.DoesNotContain(recorder.Counts, item => item.Name == "corebanking.ledger.reconciliation.mismatches");
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Contains(recorder.Counts, item => item.Name == "corebanking.ledger.reconciliation.accounts" && item.Value == total);
        Assert.Contains(recorder.Counts, item => item.Name == "corebanking.ledger.reconciliation.mismatches" && item.Value == 0);
        Assert.Contains(recorder.Counts, item => item.Name == "corebanking.ledger.reconciliation.started" && item.Value > 0);
        Assert.Contains(recorder.Counts, item => item.Name == "corebanking.ledger.reconciliation.completed" && item.Value > 0);

        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "LedgerAccounts" DISABLE TRIGGER protect_ledger_account_trigger""");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"LedgerAccounts\" SET \"BalanceMinor\" = \"BalanceMinor\" + 1 WHERE \"Id\" = {first.Id}");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "LedgerAccounts" ENABLE TRIGGER protect_ledger_account_trigger""");
        }

        await monitor.SampleAsync(CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "LedgerPostings" RENAME TO "LedgerPostingsUnavailable" """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => monitor.SampleAsync(CancellationToken.None));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "LedgerPostingsUnavailable" RENAME TO "LedgerPostings" """);
        }
        Assert.Single(recorder.Counts, item => item.Name == "corebanking.ledger.reconciliation.mismatches");
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Single(recorder.Counts, item => item.Name == "corebanking.ledger.reconciliation.mismatches");
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Equal(total, recorder.Counts.Last(item => item.Name == "corebanking.ledger.reconciliation.accounts").Value);
        Assert.Equal(1, recorder.Counts.Last(item => item.Name == "corebanking.ledger.reconciliation.mismatches").Value);
    }

    [Fact]
    public async Task HostedMonitor_EmitsSampleWhenCollectorIsSubscribed()
    {
        using var services = new ServiceCollection()
            .AddDbContext<BankingDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()))
            .BuildServiceProvider();
        using var recorder = new WorkerMetricRecorder();
        using var monitor = new LedgerReconciliationMonitor(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LedgerReconciliationMonitor>.Instance);
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!recorder.Counts.Any(item => item.Name == "corebanking.ledger.reconciliation.mismatches")
                && DateTime.UtcNow < deadline)
            {
                if (monitor.ExecuteTask is { IsFaulted: true }) await monitor.ExecuteTask;
                await Task.Delay(50);
            }
            Assert.Contains(recorder.Counts, item => item.Name == "corebanking.ledger.reconciliation.mismatches" && item.Value == 0);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }
}
