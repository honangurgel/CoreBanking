using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Worker;

public static class PixThrottle
{
    public static async Task<bool> TryAcquireAsync(BankingDbContext operationDb, IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await db.Database.SqlQueryRaw<Settings>("""
            SELECT "RequestsPerMinute", "MaxConcurrent" FROM "PixThrottleSettings" WHERE "Id" = 1 FOR UPDATE
            """).SingleAsync(cancellationToken);

        var slot = await operationDb.Database.SqlQueryRaw<int?>("""
            SELECT slot AS "Value" FROM generate_series(1, {0}) AS slot
            WHERE pg_try_advisory_xact_lock(921744, slot) LIMIT 1
            """, settings.MaxConcurrent).SingleOrDefaultAsync(cancellationToken);
        if (slot is null) return false;

        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM "PixThrottleCalls" WHERE "StartedAt" <= clock_timestamp() - interval '1 minute'
            """, cancellationToken);
        var count = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value" FROM "PixThrottleCalls"
            """).SingleAsync(cancellationToken);
        if (count >= settings.RequestsPerMinute)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "PixThrottleCalls" ("StartedAt") VALUES (clock_timestamp())
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private sealed class Settings
    {
        public int RequestsPerMinute { get; set; }
        public int MaxConcurrent { get; set; }
    }
}
