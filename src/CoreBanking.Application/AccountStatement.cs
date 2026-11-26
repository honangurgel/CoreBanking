namespace CoreBanking.Application;

public sealed record StatementItem(Guid EntryId, DateTimeOffset CreatedAt, string Description,
    long AmountMinor, Guid? ReversesEntryId);

public sealed record StatementCursor(DateTimeOffset CreatedAt, Guid EntryId);

public sealed record AccountStatement(Guid AccountId, string Currency,
    IReadOnlyList<StatementItem> Items, StatementCursor? NextCursor);

public interface IAccountStatementService
{
    Task<AccountStatement?> FindAsync(Guid accountId, int pageSize, StatementCursor? cursor,
        CancellationToken cancellationToken);
}
