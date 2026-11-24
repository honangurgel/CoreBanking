namespace CoreBanking.Application.Pix;

public static class PixRules
{
    public const long MaxAmountMinor = 99_999_999_999_999_999;
    public static bool IsInstitution(string? ispb) => ispb is "00000000" or "60701190" or "60746948";
}

public sealed record PixRequest(Guid AccountId, Guid KeyId, string? InstitutionIspb, string? Direction, long AmountMinor, string? Scenario);
public sealed record PixResult(Guid Id, Guid AccountId, Guid KeyId, string InstitutionIspb, string Direction,
    long AmountMinor, string Scenario, string Status, DateTimeOffset CreatedAt);
public enum PixOutcome { Accepted, InsufficientFunds, AccountNotFound, KeyNotFound, Conflict }
public sealed record PixResponse(PixOutcome Outcome, PixResult? Operation);
public sealed record PixStatusTransition(string Status, DateTimeOffset CreatedAt);
public sealed record PixReceipt(PixResult Operation, Guid? ReservationEntryId, Guid? SettlementEntryId, IReadOnlyList<PixStatusTransition> Transitions);
public sealed record PixKeyResult(Guid Id, Guid AccountId);

public interface IPixService
{
    Task<Guid?> CreateKeyAsync(Guid accountId, CancellationToken cancellationToken);
    Task<PixKeyResult?> FindKeyAsync(Guid id, CancellationToken cancellationToken);
    Task<PixResponse> CreateAsync(PixRequest request, string key, CancellationToken cancellationToken);
    Task<PixResult?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<PixReceipt?> FindReceiptAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken);
}
