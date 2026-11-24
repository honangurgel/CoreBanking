namespace CoreBanking.Simulator;

public static class SimulatorProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", simulated = true }));

        app.MapGet("/api/v1/institutions", () => Results.Ok(InstitutionCatalog.All));

        app.MapPost("/api/v1/settlements", async (SimulatedSettlementRequest request, CancellationToken cancellationToken) =>
        {
            if (request.OperationId == Guid.Empty || request.KeyId == Guid.Empty || request.AmountMinor <= 0
                || !InstitutionCatalog.Contains(request.InstitutionIspb)
                || request.Direction is not ("Outgoing" or "Incoming")
                || request.Scenario is not ("Confirm" or "Reject" or "Unavailable")
                || request.DelayMilliseconds is < 0 or > 10000)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["settlement"] = ["Informe operação, chave fictícia, instituição, direção, cenário e valor válidos; atraso máximo de 10000 ms."]
                });
            }

            await Task.Delay(request.DelayMilliseconds, cancellationToken);
            if (request.Scenario == "Unavailable")
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Indisponibilidade simulada", detail: "Nenhuma liquidação real foi solicitada.");
            }

            return Results.Ok(new SimulatedSettlementResult(request.OperationId,
                request.Scenario == "Confirm" ? "Confirmed" : "Rejected", true));
        });

        app.Run();
    }
}
