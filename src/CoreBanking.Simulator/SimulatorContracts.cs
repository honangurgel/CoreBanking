namespace CoreBanking.Simulator;

public sealed record Institution(string Ispb, string Name);

public static class InstitutionCatalog
{
    public static IReadOnlyList<Institution> All { get; } = Array.AsReadOnly<Institution>(
    [
        new("00000000", "Banco do Brasil S.A."),
        new("60701190", "Itaú Unibanco S.A."),
        new("60746948", "Banco Bradesco S.A.")
    ]);

    public static bool Contains(string? ispb) => All.Any(institution => institution.Ispb == ispb);
}

public sealed record SimulatedSettlementRequest(
    Guid OperationId, Guid KeyId, string? InstitutionIspb, string? Direction,
    long AmountMinor, string? Scenario, int DelayMilliseconds);

public sealed record SimulatedSettlementResult(Guid OperationId, string Status, bool Simulated);

public sealed class SimulatorAssemblyMarker;
