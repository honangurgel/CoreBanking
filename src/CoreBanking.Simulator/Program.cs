namespace CoreBanking.Simulator;

public static class SimulatorProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddConsole(options => options.FormatterName = "json");
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", simulated = true }));

        app.MapGet("/api/v1/institutions", () => Results.Ok(InstitutionCatalog.All));

        app.MapPost("/api/v1/settlements", async (SimulatedSettlementRequest request, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
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
            var logger = loggerFactory.CreateLogger("CoreBanking.Simulator.Settlements");
            if (request.Scenario == "Unavailable")
            {
                logger.LogWarning("Simulador indisponível para Pix {OperationId}.", request.OperationId);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Indisponibilidade simulada", detail: "Nenhuma liquidação real foi solicitada.");
            }

            var status = request.Scenario == "Confirm" ? "Confirmed" : "Rejected";
            logger.LogInformation("Simulador retornou {Status} para Pix {OperationId}.", status, request.OperationId);
            return Results.Ok(new SimulatedSettlementResult(request.OperationId, status, true));
        });

        app.Run();
    }
}
