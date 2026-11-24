using CoreBanking.Domain.Ledger;

namespace CoreBanking.UnitTests;

public class LedgerEntryTests
{
    [Fact]
    public void Entry_RejectsUnbalancedOrRepeatedAccounts()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => LedgerEntry.Create("Teste",
            [new(first, 100), new(second, -99)]));
        Assert.Throws<ArgumentException>(() => LedgerEntry.Create("Teste",
            [new(first, 100), new(first, -100)]));
        Assert.Throws<ArgumentException>(() => LedgerEntry.Create("Teste", [new(first, 100)]));
    }

    [Fact]
    public void Posting_RejectsZeroAndNonReversibleValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerPosting(Guid.NewGuid(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerPosting(Guid.NewGuid(), long.MinValue));
        Assert.Throws<ArgumentException>(() => new LedgerPosting(Guid.Empty, 1));
    }

    [Fact]
    public void Entry_UsesSnapshotAndCreatesBalancedReversal()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var postings = new List<LedgerPosting> { new(first, 100), new(second, -100) };
        var entry = LedgerEntry.Create("  Depósito fictício  ", postings);
        postings.Clear();

        var reversal = entry.CreateReversal("Estorno fictício");
        Assert.Equal("Depósito fictício", entry.Description);
        Assert.Equal(2, entry.Postings.Count);
        Assert.NotEqual(entry.Id, reversal.Id);
        Assert.Equal(entry.Id, reversal.ReversesEntryId);
        Assert.Equal(-100, reversal.Postings[0].AmountMinor);
        Assert.Equal(100, reversal.Postings[1].AmountMinor);
    }

    [Fact]
    public void Entry_ValidatesBalanceWithoutIntegerOverflow()
    {
        var entry = LedgerEntry.Create("Valores grandes",
        [
            new(Guid.NewGuid(), long.MaxValue),
            new(Guid.NewGuid(), long.MaxValue),
            new(Guid.NewGuid(), -long.MaxValue),
            new(Guid.NewGuid(), -long.MaxValue)
        ]);
        Assert.Equal(4, entry.Postings.Count);
    }
}
