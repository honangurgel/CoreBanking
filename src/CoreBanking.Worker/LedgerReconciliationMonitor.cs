using System.Data.Common;
using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Worker;

public sealed class LedgerReconciliationMonitor(
    IServiceScopeFactory scopeFactory, ILogger<LedgerReconciliationMonitor> logger) : BackgroundService
{
    private const int BatchSize = 100;
    private Guid? cursor;
    private long checkedAccounts;
    private long mismatches;
    private long started;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            if (WorkerMetrics.LedgerReconciliationEnabled)
            {
                try
                {
                    await SampleAsync(stoppingToken);
                }
                catch (DbException exception)
                {
                    logger.LogError(exception, "Não foi possível reconciliar os saldos do ledger.");
                }
            }
            else
            {
                Reset();
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        if (cursor is null) started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        List<AccountSample> batch;
        try
        {
            var hasCursor = cursor.HasValue;
            var after = cursor.GetValueOrDefault();
            batch = await db.Database.SqlQuery<AccountSample>($"""
                SELECT a."Id", a."BalanceMinor" <> COALESCE((
                    SELECT SUM(p."AmountMinor")
                    FROM "LedgerPostings" p JOIN "LedgerEntries" e ON e."Id" = p."EntryId"
                    WHERE p."LedgerAccountId" = a."Id" AND e."Posted"
                ), 0) AS "Mismatch"
                FROM (
                    SELECT "Id", "BalanceMinor" FROM "LedgerAccounts"
                    WHERE NOT {hasCursor} OR "Id" > {after}
                    ORDER BY "Id" LIMIT {BatchSize}
                ) a
                ORDER BY a."Id"
                """).ToListAsync(cancellationToken);
        }
        catch (DbException)
        {
            Reset();
            throw;
        }

        checkedAccounts += batch.Count;
        mismatches += batch.Count(item => item.Mismatch);
        if (batch.Count == BatchSize)
        {
            cursor = batch[^1].Id;
            return;
        }

        WorkerMetrics.RecordLedgerReconciliation(checkedAccounts, mismatches, started, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (mismatches > 0)
            logger.LogWarning("Reconciliação do ledger encontrou {MismatchCount} contas divergentes entre {AccountCount} contas verificadas.",
                mismatches, checkedAccounts);
        Reset();
    }

    private void Reset()
    {
        cursor = null;
        checkedAccounts = 0;
        mismatches = 0;
        started = 0;
    }

    private sealed class AccountSample
    {
        public Guid Id { get; set; }
        public bool Mismatch { get; set; }
    }
}
