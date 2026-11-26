using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoreBanking.Domain.Ledger;
using CoreBanking.Infrastructure;
using CoreBanking.Infrastructure.Pix;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBanking.Worker;

public sealed class PixWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<PixWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var address = configuration["Pix:SimulatorUrl"];
        if (string.IsNullOrWhiteSpace(address))
        {
            logger.LogInformation("Processamento Pix desativado; configure Pix:SimulatorUrl para o simulador local.");
            return;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Pix:SimulatorUrl deve ser uma origem HTTP local (loopback), sem caminho.");

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(5)
        };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(scopeFactory, client, logger, stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (NpgsqlException exception) when (exception.IsTransient)
            {
                logger.LogError(exception, "Banco indisponível no processamento Pix; aguardando reconexão.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is NpgsqlException { IsTransient: true })
            {
                logger.LogError(exception, "Falha ao persistir Pix; aguardando reconexão.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    public static async Task<bool> ProcessNextAsync(IServiceScopeFactory scopeFactory, HttpClient client,
        ILogger logger, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var operation = await db.PixOperations.FromSqlRaw("""
            SELECT * FROM "PixOperations"
            WHERE "Status" = 'Pending' AND "NextAttemptAt" <= now()
            ORDER BY "NextAttemptAt", "Id"
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(cancellationToken);
        if (operation is null) return false;
        if (!await PixThrottle.TryAcquireAsync(db, scopeFactory, cancellationToken))
            return false;

        SimulatedResult? result = null;
        var requiresReview = false;
        try
        {
            using var response = await client.PostAsJsonAsync("api/v1/settlements", new
            {
                OperationId = operation.Id,
                operation.KeyId,
                operation.InstitutionIspb,
                operation.Direction,
                operation.AmountMinor,
                operation.Scenario,
                DelayMilliseconds = 0
            }, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                result = await response.Content.ReadFromJsonAsync<SimulatedResult>(cancellationToken);
                if (result is null || result.OperationId != operation.Id || !result.Simulated ||
                    result.Status is not ("Confirmed" or "Rejected") ||
                    (result.Status == "Confirmed") != (operation.Scenario == "Confirm"))
                {
                    logger.LogError("Resposta inválida ou inconsistente do simulador para Pix {OperationId}.", operation.Id);
                    result = null;
                    requiresReview = true;
                }
            }
            else if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("Simulador indisponível para Pix {OperationId}: HTTP {Status}.", operation.Id, response.StatusCode);
            }
            else
            {
                logger.LogError("Simulador rejeitou requisição Pix {OperationId}: HTTP {Status}.", operation.Id, response.StatusCode);
                requiresReview = true;
            }
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Falha de comunicação com o simulador para Pix {OperationId}.", operation.Id);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Tempo esgotado no simulador para Pix {OperationId}.", operation.Id);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Resposta inválida do simulador para Pix {OperationId}.", operation.Id);
            requiresReview = true;
        }
        catch (NotSupportedException exception)
        {
            logger.LogError(exception, "Formato de resposta não suportado para Pix {OperationId}.", operation.Id);
            requiresReview = true;
        }

        if (result is null)
        {
            operation.Attempts = requiresReview ? 3 : operation.Attempts + 1;
            if (operation.Attempts >= 3)
            {
                operation.Status = "RequiresReview";
                PixService.AddTransition(db, operation);
            }
            else
            {
                var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
                operation.NextAttemptAt = now.AddSeconds(Math.Pow(2, operation.Attempts - 1));
            }
        }
        else
        {
            var ledger = await db.LedgerAccounts.AsNoTracking().SingleAsync(item => item.CustomerAccountId == operation.AccountId, cancellationToken);
            LedgerEntry? entry = (operation.Direction, result.Status) switch
            {
                ("Outgoing", "Confirmed") => LedgerEntry.Create("Liquidação de Pix fictício enviado",
                    [new(PixService.ReserveAccountId, -operation.AmountMinor), new(PixService.ClearingAccountId, operation.AmountMinor)]),
                ("Outgoing", "Rejected") => LedgerEntry.Create("Liberação de reserva de Pix fictício",
                    [new(PixService.ReserveAccountId, -operation.AmountMinor), new(ledger.Id, operation.AmountMinor)]),
                ("Incoming", "Confirmed") => LedgerEntry.Create("Liquidação de Pix fictício recebido",
                    [new(PixService.ClearingAccountId, -operation.AmountMinor), new(ledger.Id, operation.AmountMinor)]),
                ("Incoming", "Rejected") => null,
                _ => throw new InvalidOperationException("Operação Pix inválida.")
            };
            if (entry is not null)
            {
                await new LedgerWriter(db).PostWithinTransactionAsync(entry, cancellationToken);
                operation.SettlementEntryId = entry.Id;
            }
            operation.Status = result.Status;
            PixService.AddTransition(db, operation);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Pix {OperationId} processado com estado {Status} e {Attempts} falhas.",
            operation.Id, operation.Status, operation.Attempts);
        return true;
    }

    private sealed record SimulatedResult(Guid OperationId, string Status, bool Simulated);
}
