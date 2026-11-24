using System.Collections.ObjectModel;
using System.Numerics;

namespace CoreBanking.Domain.Ledger;

public sealed class LedgerEntry
{
    private LedgerEntry(Guid id, string description, DateTimeOffset createdAt, ReadOnlyCollection<LedgerPosting> postings, Guid? reversesEntryId)
    {
        Id = id;
        Description = description;
        CreatedAt = createdAt;
        Postings = postings;
        ReversesEntryId = reversesEntryId;
    }

    public Guid Id { get; }
    public string Description { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<LedgerPosting> Postings { get; }
    public Guid? ReversesEntryId { get; }

    public static LedgerEntry Create(string? description, IEnumerable<LedgerPosting> postings) =>
        CreateCore(description, postings, null);

    private static LedgerEntry CreateCore(string? description, IEnumerable<LedgerPosting> postings, Guid? reversesEntryId)
    {
        var normalizedDescription = description?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDescription) || normalizedDescription.Length > 200)
        {
            throw new ArgumentException("A descrição deve conter entre 1 e 200 caracteres.", nameof(description));
        }

        ArgumentNullException.ThrowIfNull(postings);
        var items = postings.ToArray();
        if (items.Length < 2 || items.Any(item => item is null))
        {
            throw new ArgumentException("O lançamento deve conter pelo menos duas partidas.", nameof(postings));
        }

        if (items.Select(item => item.LedgerAccountId).Distinct().Count() != items.Length)
        {
            throw new ArgumentException("Cada conta contábil deve aparecer apenas uma vez no lançamento.", nameof(postings));
        }

        if (items.Aggregate(BigInteger.Zero, (sum, item) => sum + item.AmountMinor) != BigInteger.Zero)
        {
            throw new ArgumentException("As partidas devem estar balanceadas.", nameof(postings));
        }

        return new LedgerEntry(Guid.NewGuid(), normalizedDescription, DateTimeOffset.UtcNow, Array.AsReadOnly(items), reversesEntryId);
    }

    public LedgerEntry CreateReversal(string description) =>
        CreateCore(description, Postings.Select(posting => new LedgerPosting(posting.LedgerAccountId, -posting.AmountMinor)), Id);
}
