using CoreBanking.Application;
using CoreBanking.Application.Pix;
using CoreBanking.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure.Pix;

public sealed class PixService(BankingDbContext db) : IPixService
{
    public static readonly Guid ReserveAccountId = Guid.Parse("9c48bd10-5d28-4c64-94da-aaf778039200");
    public static readonly Guid ClearingAccountId = Guid.Parse("9c48bd10-5d28-4c64-94da-aaf778039201");

    public async Task<Guid?> CreateKeyAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!await db.Accounts.AnyAsync(account => account.Id == accountId, cancellationToken)) return null;
        var key = new PixKey { Id = Guid.NewGuid(), AccountId = accountId };
        db.PixKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return key.Id;
    }

    public Task<PixKeyResult?> FindKeyAsync(Guid id, CancellationToken cancellationToken) =>
        db.PixKeys.AsNoTracking().Where(key => key.Id == id)
            .Select(key => new PixKeyResult(key.Id, key.AccountId)).SingleOrDefaultAsync(cancellationToken);

    public async Task<PixResult?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var operation = await db.PixOperations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return operation is null ? null : ToResult(operation);
    }

    public async Task<PixReceipt?> FindReceiptAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var operation = await db.PixOperations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (operation is null) return null;
        var transitions = await db.PixTransitions.AsNoTracking().Where(item => item.OperationId == id)
            .OrderBy(item => item.Id)
            .Select(item => new PixStatusTransition(item.Status, item.CreatedAt)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PixReceipt(ToResult(operation), operation.ReservationEntryId, operation.SettlementEntryId, transitions);
    }

    public async Task<PixResponse> CreateAsync(PixRequest request, string key, AuditActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var scopedKey = $"{request.AccountId:D}:{key}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scopedKey}, 921743))", cancellationToken);
        var existing = await db.PixOperations.AsNoTracking().SingleOrDefaultAsync(
            item => item.AccountId == request.AccountId && item.IdempotencyKey == key, cancellationToken);
        if (existing is not null)
            return ExistingResponse(existing, request);

        if (!await db.Accounts.AnyAsync(account => account.Id == request.AccountId, cancellationToken))
            return new(PixOutcome.AccountNotFound, null);
        if (request.Direction == "Incoming" && !await db.PixKeys.AnyAsync(
            item => item.Id == request.KeyId && item.AccountId == request.AccountId, cancellationToken))
            return new(PixOutcome.KeyNotFound, null);

        var id = Guid.NewGuid();
        var clock = DateTimeOffset.UtcNow;
        var now = clock.AddTicks(-(clock.Ticks % 10));
        var traceParent = TraceContext.Capture();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PixOperations" ("Id", "IdempotencyKey", "AccountId", "KeyId", "InstitutionIspb", "Direction",
                "AmountMinor", "Scenario", "Status", "CreatedAt", "Attempts", "NextAttemptAt", "TraceParent")
            VALUES ({id}, {key}, {request.AccountId}, {request.KeyId}, {request.InstitutionIspb}, {request.Direction},
                {request.AmountMinor}, {request.Scenario}, {"Pending"}, {now}, {0}, now(), {traceParent})
            ON CONFLICT ("AccountId", "IdempotencyKey") DO NOTHING
            """, cancellationToken);
        if (inserted == 0)
        {
            var concurrent = await db.PixOperations.AsNoTracking().SingleAsync(
                item => item.AccountId == request.AccountId && item.IdempotencyKey == key, cancellationToken);
            return ExistingResponse(concurrent, request);
        }

        var operation = await db.PixOperations.SingleAsync(item => item.Id == id, cancellationToken);
        if (request.Direction == "Outgoing")
        {
            var accounts = await db.LedgerAccounts.FromSqlInterpolated($"""
                SELECT * FROM "LedgerAccounts"
                WHERE "CustomerAccountId" = {request.AccountId} OR "Id" = {ReserveAccountId}
                ORDER BY "Id" FOR UPDATE
                """).AsNoTracking().ToListAsync(cancellationToken);
            var ledger = accounts.Single(item => item.CustomerAccountId == request.AccountId);
            if (!accounts.Any(item => item.Id == ReserveAccountId && item.CustomerAccountId is null))
                throw new InvalidOperationException("Conta de reserva Pix inexistente ou inválida.");
            if (ledger.BalanceMinor < request.AmountMinor)
            {
                operation.Status = "InsufficientFunds";
            }
            else
            {
                var entry = LedgerEntry.Create("Reserva de Pix fictício",
                    [new(ledger.Id, -request.AmountMinor), new(ReserveAccountId, request.AmountMinor)]);
                await new LedgerWriter(db).PostWithinTransactionAsync(entry, cancellationToken);
                operation.ReservationEntryId = entry.Id;
            }
        }
        AddTransition(db, operation);
        await db.SaveChangesAsync(cancellationToken);
        await OperationAuditWriter.WriteAsync(db, actor, "PixCreate", operation.Status, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(operation.Status == "InsufficientFunds" ? PixOutcome.InsufficientFunds : PixOutcome.Accepted, ToResult(operation));
    }

    public async Task<bool> RetryAsync(Guid id, AuditActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var operation = await db.PixOperations.FromSqlInterpolated(
            $"""SELECT * FROM "PixOperations" WHERE "Id" = {id} FOR UPDATE""").SingleOrDefaultAsync(cancellationToken);
        if (operation is null || operation.Status != "RequiresReview") return false;
        operation.Status = "Pending";
        operation.TraceParent = TraceContext.Capture() ?? operation.TraceParent;
        operation.Attempts = 0;
        operation.NextAttemptAt = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"").SingleAsync(cancellationToken);
        AddTransition(db, operation);
        await db.SaveChangesAsync(cancellationToken);
        await OperationAuditWriter.WriteAsync(db, actor, "PixRetry", "Pending", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public static void AddTransition(BankingDbContext db, PixOperation operation) =>
        db.PixTransitions.Add(new PixTransition { OperationId = operation.Id, Status = operation.Status, CreatedAt = DateTimeOffset.UtcNow });

    public static PixResult ToResult(PixOperation operation) =>
        new(operation.Id, operation.AccountId, operation.KeyId, operation.InstitutionIspb, operation.Direction,
            operation.AmountMinor, operation.Scenario, operation.Status, operation.CreatedAt);

    private static PixResponse ExistingResponse(PixOperation existing, PixRequest request)
    {
        if (existing.AccountId != request.AccountId || existing.KeyId != request.KeyId ||
            existing.InstitutionIspb != request.InstitutionIspb || existing.Direction != request.Direction ||
            existing.AmountMinor != request.AmountMinor || existing.Scenario != request.Scenario)
            return new(PixOutcome.Conflict, null);
        return new(existing.Status == "InsufficientFunds" ? PixOutcome.InsufficientFunds : PixOutcome.Accepted, ToResult(existing));
    }
}
