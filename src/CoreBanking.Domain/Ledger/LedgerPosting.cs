namespace CoreBanking.Domain.Ledger;

public sealed record LedgerPosting
{
    public LedgerPosting(Guid ledgerAccountId, long amountMinor)
    {
        if (ledgerAccountId == Guid.Empty)
        {
            throw new ArgumentException("A conta contábil é obrigatória.", nameof(ledgerAccountId));
        }

        if (amountMinor is 0 or long.MinValue)
        {
            throw new ArgumentOutOfRangeException(nameof(amountMinor), "O valor deve ser diferente de zero e reversível.");
        }

        LedgerAccountId = ledgerAccountId;
        AmountMinor = amountMinor;
    }

    public Guid LedgerAccountId { get; }
    public long AmountMinor { get; }
}
