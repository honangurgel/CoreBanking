using System.Data.Common;
using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Worker;

public sealed class BacklogMonitor(IServiceScopeFactory scopeFactory, ILogger<BacklogMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            if (WorkerMetrics.BacklogEnabled)
            {
                try
                {
                    await SampleAsync(stoppingToken);
                }
                catch (DbException exception)
                {
                    logger.LogError(exception, "Não foi possível coletar as métricas das filas persistidas.");
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var snapshot = await db.Database.SqlQueryRaw<BacklogSnapshot>("""
            SELECT pix."Count" AS "PixCount", pix."Age" AS "PixAge",
                outbox."Count" AS "OutboxCount", outbox."Age" AS "OutboxAge"
            FROM (SELECT count(*) AS "Count",
                    COALESCE(GREATEST(0, EXTRACT(EPOCH FROM clock_timestamp() - min("CreatedAt"))), 0)::double precision AS "Age"
                  FROM "PixOperations" WHERE "Status" = 'Pending') AS pix
            CROSS JOIN (SELECT count(*) AS "Count",
                    COALESCE(GREATEST(0, EXTRACT(EPOCH FROM clock_timestamp() - min("CreatedAt"))), 0)::double precision AS "Age"
                  FROM "OutboxMessages" WHERE "PublishedAt" IS NULL) AS outbox
            """).SingleAsync(cancellationToken);
        WorkerMetrics.RecordBacklog("pix", snapshot.PixCount, snapshot.PixAge);
        WorkerMetrics.RecordBacklog("outbox", snapshot.OutboxCount, snapshot.OutboxAge);
    }

    private sealed class BacklogSnapshot
    {
        public long PixCount { get; set; }
        public double PixAge { get; set; }
        public long OutboxCount { get; set; }
        public double OutboxAge { get; set; }
    }
}
