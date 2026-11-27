namespace CoreBanking.Application.Cards;

public static class DebitCardRules
{
    public const long MaxAmountMinor = 99_999_999_999_999_999;
}

public sealed record DebitCardResult(Guid Id, Guid AccountId, DateTimeOffset CreatedAt);
public sealed record DebitAuthorizationResult(Guid Id, Guid CardId, Guid AccountId, long AmountMinor,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset? FinalizedAt,
    Guid? ReservationEntryId, Guid? FinalizationEntryId);
public enum DebitCardOutcome { Created, Authorized, InsufficientFunds, Settled, Cancelled, NotFound, Conflict }
public sealed record DebitCardResponse(DebitCardOutcome Outcome, DebitCardResult? Card);
public sealed record DebitAuthorizationResponse(DebitCardOutcome Outcome, DebitAuthorizationResult? Authorization);

public interface IDebitCardService
{
    Task<DebitCardResponse> IssueAsync(Guid accountId, string key, AuditActor actor, CancellationToken cancellationToken);
    Task<DebitCardResult?> FindCardAsync(Guid cardId, CancellationToken cancellationToken);
    Task<DebitAuthorizationResponse> AuthorizeAsync(Guid cardId, long amountMinor, string key, AuditActor actor,
        CancellationToken cancellationToken);
    Task<DebitAuthorizationResult?> FindAuthorizationAsync(Guid id, CancellationToken cancellationToken);
    Task<DebitAuthorizationResponse> FinalizeAsync(Guid id, bool settle, AuditActor actor, CancellationToken cancellationToken);
}
