namespace CoreBanking.Application.Deposits;

public sealed record DepositResult(Guid Id, Guid AccountId, long AmountMinor, DateTimeOffset CreatedAt);

public enum DepositOutcome
{
    Completed,
    AccountNotFound,
    IdempotencyConflict
}

public sealed record DepositResponse(DepositOutcome Outcome, DepositResult? Deposit);

public interface IDepositService
{
    Task<DepositResponse> CreateAsync(Guid accountId, long amountMinor, string key, CancellationToken cancellationToken);
    Task<DepositResult?> FindAsync(Guid id, CancellationToken cancellationToken);
}
