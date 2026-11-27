using CoreBanking.Application;
using CoreBanking.Application.Cards;
using CoreBanking.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure.Cards;

public sealed class DebitCardService(BankingDbContext db) : IDebitCardService
{
    public static readonly Guid ReserveAccountId = Guid.Parse("9c48bd10-5d28-4c64-94da-aaf778039300");
    public static readonly Guid ClearingAccountId = Guid.Parse("9c48bd10-5d28-4c64-94da-aaf778039301");

    public async Task<DebitCardResponse> IssueAsync(Guid accountId, string key, AuditActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockKeyAsync("card", accountId, key, cancellationToken);
        var existing = await db.DebitCards.AsNoTracking().SingleOrDefaultAsync(
            item => item.AccountId == accountId && item.IdempotencyKey == key, cancellationToken);
        if (existing is not null) return new(DebitCardOutcome.Created, ToResult(existing));
        if (!await db.Accounts.AsNoTracking().AnyAsync(item => item.Id == accountId, cancellationToken))
            return new(DebitCardOutcome.NotFound, null);

        var card = new DebitCardRecord
        {
            Id = Guid.NewGuid(), AccountId = accountId, IdempotencyKey = key, CreatedAt = Now()
        };
        db.DebitCards.Add(card);
        await db.SaveChangesAsync(cancellationToken);
        await OperationAuditWriter.WriteAsync(db, actor, "CardIssue", "Created", card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(DebitCardOutcome.Created, ToResult(card));
    }

    public async Task<DebitCardResult?> FindCardAsync(Guid cardId, CancellationToken cancellationToken)
    {
        var card = await db.DebitCards.AsNoTracking().SingleOrDefaultAsync(item => item.Id == cardId, cancellationToken);
        return card is null ? null : ToResult(card);
    }

    public async Task<DebitAuthorizationResponse> AuthorizeAsync(Guid cardId, long amountMinor, string key,
        AuditActor actor, CancellationToken cancellationToken)
    {
        if (amountMinor is <= 0 or > DebitCardRules.MaxAmountMinor)
            throw new ArgumentOutOfRangeException(nameof(amountMinor));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var card = await db.DebitCards.AsNoTracking().SingleOrDefaultAsync(item => item.Id == cardId, cancellationToken);
        if (card is null) return new(DebitCardOutcome.NotFound, null);
        await LockKeyAsync("authorization", card.AccountId, key, cancellationToken);
        var existing = await db.DebitAuthorizations.AsNoTracking().SingleOrDefaultAsync(
            item => item.AccountId == card.AccountId && item.IdempotencyKey == key, cancellationToken);
        if (existing is not null)
        {
            if (existing.CardId != cardId || existing.AmountMinor != amountMinor)
                return new(DebitCardOutcome.Conflict, null);
            return new(existing.Status == "InsufficientFunds" ? DebitCardOutcome.InsufficientFunds : DebitCardOutcome.Authorized,
                ToResult(existing));
        }

        var accounts = await LockAccountsAsync(card.AccountId, ReserveAccountId, cancellationToken);
        var customer = accounts.SingleOrDefault(item => item.CustomerAccountId == card.AccountId)
            ?? throw new InvalidOperationException("Conta contábil do cartão inexistente.");
        EnsureInternalAccount(accounts, ReserveAccountId);

        var authorization = new DebitAuthorization
        {
            Id = Guid.NewGuid(), CardId = card.Id, AccountId = card.AccountId, IdempotencyKey = key,
            AmountMinor = amountMinor, CreatedAt = Now()
        };
        if (customer.BalanceMinor < amountMinor)
        {
            authorization.Status = "InsufficientFunds";
        }
        else
        {
            var entry = LedgerEntry.Create("Reserva de cartão de débito fictício",
                [new(customer.Id, -amountMinor), new(ReserveAccountId, amountMinor)]);
            await new LedgerWriter(db).PostWithinTransactionAsync(entry, cancellationToken);
            authorization.ReservationEntryId = entry.Id;
            authorization.Status = "Authorized";
        }
        db.DebitAuthorizations.Add(authorization);
        await db.SaveChangesAsync(cancellationToken);
        await OperationAuditWriter.WriteAsync(db, actor, "CardAuthorize", authorization.Status, authorization.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(authorization.Status == "InsufficientFunds" ? DebitCardOutcome.InsufficientFunds : DebitCardOutcome.Authorized,
            ToResult(authorization));
    }

    public async Task<DebitAuthorizationResult?> FindAuthorizationAsync(Guid id, CancellationToken cancellationToken)
    {
        var operation = await db.DebitAuthorizations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return operation is null ? null : ToResult(operation);
    }

    public async Task<DebitAuthorizationResponse> FinalizeAsync(Guid id, bool settle, AuditActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var authorization = await db.DebitAuthorizations.FromSqlInterpolated(
            $"""SELECT * FROM "DebitAuthorizations" WHERE "Id" = {id} FOR UPDATE""").SingleOrDefaultAsync(cancellationToken);
        if (authorization is null) return new(DebitCardOutcome.NotFound, null);
        var target = settle ? "Settled" : "Cancelled";
        var outcome = settle ? DebitCardOutcome.Settled : DebitCardOutcome.Cancelled;
        if (authorization.Status == target) return new(outcome, ToResult(authorization));
        if (authorization.Status is "Settled" or "Cancelled" or "InsufficientFunds")
            return new(DebitCardOutcome.Conflict, ToResult(authorization));
        if (authorization.Status != "Authorized")
            throw new InvalidOperationException($"Estado inesperado da autorização de cartão {id}: {authorization.Status}.");

        var accounts = await LockAccountsAsync(authorization.AccountId,
            settle ? ClearingAccountId : ReserveAccountId, cancellationToken);
        var reserve = EnsureInternalAccount(accounts, ReserveAccountId);
        if (reserve.BalanceMinor < authorization.AmountMinor)
            throw new InvalidOperationException("Saldo da conta de reserva de cartão inconsistente.");
        var destination = settle
            ? EnsureInternalAccount(accounts, ClearingAccountId)
            : accounts.SingleOrDefault(item => item.CustomerAccountId == authorization.AccountId)
                ?? throw new InvalidOperationException("Conta contábil do cartão inexistente.");
        var entry = LedgerEntry.Create(settle ? "Liquidação de cartão de débito fictício" : "Cancelamento de cartão de débito fictício",
            [new(ReserveAccountId, -authorization.AmountMinor), new(destination.Id, authorization.AmountMinor)]);
        await new LedgerWriter(db).PostWithinTransactionAsync(entry, cancellationToken);
        authorization.Status = target;
        authorization.FinalizationEntryId = entry.Id;
        authorization.FinalizedAt = Now();
        await db.SaveChangesAsync(cancellationToken);
        await OperationAuditWriter.WriteAsync(db, actor, settle ? "CardSettle" : "CardCancel", target, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(outcome, ToResult(authorization));
    }

    private Task LockKeyAsync(string operation, Guid accountId, string key, CancellationToken cancellationToken)
    {
        var scope = $"{operation}:{accountId:D}:{key}";
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({scope}, 921744))", cancellationToken);
    }

    private Task<List<LedgerAccount>> LockAccountsAsync(Guid accountId, Guid otherId, CancellationToken cancellationToken) =>
        db.LedgerAccounts.FromSqlInterpolated($"""
            SELECT * FROM "LedgerAccounts"
            WHERE "CustomerAccountId" = {accountId} OR "Id" IN ({ReserveAccountId}, {otherId})
            ORDER BY "Id" FOR UPDATE
            """).AsNoTracking().ToListAsync(cancellationToken);

    private static LedgerAccount EnsureInternalAccount(List<LedgerAccount> accounts, Guid id) =>
        accounts.SingleOrDefault(item => item.Id == id && item.CustomerAccountId is null)
        ?? throw new InvalidOperationException($"Conta contábil interna de cartão {id} inexistente ou inválida.");

    private static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % 10));
    }

    private static DebitCardResult ToResult(DebitCardRecord card) => new(card.Id, card.AccountId, card.CreatedAt);
    private static DebitAuthorizationResult ToResult(DebitAuthorization operation) =>
        new(operation.Id, operation.CardId, operation.AccountId, operation.AmountMinor, operation.Status,
            operation.CreatedAt, operation.FinalizedAt, operation.ReservationEntryId, operation.FinalizationEntryId);
}
