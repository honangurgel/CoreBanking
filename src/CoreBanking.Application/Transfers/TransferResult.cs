namespace CoreBanking.Application.Transfers;

public sealed record TransferResult(Guid Id, Guid SourceAccountId, Guid DestinationAccountId, long AmountMinor, string Status, DateTimeOffset CreatedAt);

public enum TransferOutcome
{
    Completed,
    InsufficientFunds,
    AccountNotFound,
    IdempotencyConflict
}

public sealed record TransferResponse(TransferOutcome Outcome, TransferResult? Transfer);

public interface ITransferService
{
    Task<TransferResponse> CreateAsync(Guid sourceAccountId, Guid destinationAccountId, long amountMinor, string key, AuditActor actor, CancellationToken cancellationToken);
    Task<TransferResult?> FindAsync(Guid id, CancellationToken cancellationToken);
}
