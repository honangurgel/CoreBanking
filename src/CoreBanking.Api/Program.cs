using CoreBanking.Application;
using CoreBanking.Application.Deposits;
using CoreBanking.Application.Pix;
using CoreBanking.Application.Transfers;
using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using CoreBanking.Infrastructure;
using CoreBanking.Infrastructure.Deposits;
using CoreBanking.Infrastructure.Pix;
using CoreBanking.Infrastructure.Transfers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<BankingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Banking")
        ?? throw new InvalidOperationException("Configure a conexão ConnectionStrings:Banking.")));
builder.Services.AddScoped<IBankingRepository, BankingRepository>();
builder.Services.AddScoped<ITransferService, TransferService>();
builder.Services.AddScoped<IDepositService, DepositService>();
builder.Services.AddScoped<IPixService, PixService>();

var permitsPerMinute = builder.Configuration.GetValue<int?>("RateLimiting:PermitsPerMinute") ?? 600;
if (permitsPerMinute < 1)
{
    throw new InvalidOperationException("RateLimiting:PermitsPerMinute deve ser maior que zero.");
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/health")
            ? RateLimitPartition.GetNoLimiter("health")
            : RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
});

var app = builder.Build();

app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => TypedResults.Ok(new { status = "Healthy" }))
    .WithName("Health");

app.MapPost("/api/v1/customers", async (CreateCustomerRequest request, IBankingRepository repository, CancellationToken cancellationToken) =>
{
    Customer customer;
    try
    {
        customer = Customer.Create(request.Name);
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [exception.Message] });
    }

    await repository.AddCustomerAsync(customer, cancellationToken);
    return Results.Created($"/api/v1/customers/{customer.Id}", customer);
});

app.MapGet("/api/v1/customers/{customerId:guid}", async (Guid customerId, IBankingRepository repository, CancellationToken cancellationToken) =>
{
    var customer = await repository.FindCustomerAsync(customerId, cancellationToken);
    return customer is null ? Results.NotFound() : Results.Ok(customer);
});

app.MapPost("/api/v1/accounts", async (OpenAccountRequest request, IBankingRepository repository, CancellationToken cancellationToken) =>
{
    if (request.CustomerId == Guid.Empty)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["customerId"] = ["O identificador do cliente é obrigatório."] });
    }

    if (await repository.FindCustomerAsync(request.CustomerId, cancellationToken) is null)
    {
        return Results.NotFound(new { message = "Cliente não encontrado." });
    }

    var account = Account.Open(request.CustomerId);
    await repository.AddAccountAsync(account, cancellationToken);
    return Results.Created($"/api/v1/accounts/{account.Id}", new AccountResponse(account.Id, account.CustomerId, account.Status.ToString(), account.OpenedAt, 0, "BRL"));
});

app.MapGet("/api/v1/accounts/{accountId:guid}", async (Guid accountId, IBankingRepository repository, CancellationToken cancellationToken) =>
{
    var details = await repository.FindAccountAsync(accountId, cancellationToken);
    return details is null
        ? Results.NotFound()
        : Results.Ok(new AccountResponse(details.Account.Id, details.Account.CustomerId, details.Account.Status.ToString(), details.Account.OpenedAt, details.BalanceMinor, "BRL"));
});

app.MapPost("/api/v1/transfers", async (CreateTransferRequest request, HttpRequest httpRequest, ITransferService transfers, CancellationToken cancellationToken) =>
{
    if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1
        || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 128 || keys[0] != keys[0]!.Trim())
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Informe uma chave única de 1 a 128 caracteres, sem espaços nas extremidades."] });
    }

    if (request.SourceAccountId == Guid.Empty || request.DestinationAccountId == Guid.Empty
        || request.SourceAccountId == request.DestinationAccountId || request.AmountMinor <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["transfer"] = ["Informe contas distintas e um valor positivo em centavos."] });
    }

    var result = await transfers.CreateAsync(request.SourceAccountId, request.DestinationAccountId, request.AmountMinor, keys[0]!, cancellationToken);
    return result.Outcome switch
    {
        TransferOutcome.Completed => Results.Created($"/api/v1/transfers/{result.Transfer!.Id}", result.Transfer),
        TransferOutcome.InsufficientFunds => Results.Conflict(result.Transfer),
        TransferOutcome.AccountNotFound => Results.NotFound(new { message = "Conta não encontrada." }),
        TransferOutcome.IdempotencyConflict => Results.Conflict(new { message = "A chave já foi utilizada com outros dados." }),
        _ => throw new InvalidOperationException("Resultado de transferência desconhecido.")
    };
});

app.MapGet("/api/v1/transfers/{transferId:guid}", async (Guid transferId, ITransferService transfers, CancellationToken cancellationToken) =>
{
    var transfer = await transfers.FindAsync(transferId, cancellationToken);
    return transfer is null ? Results.NotFound() : Results.Ok(transfer);
});

app.MapPost("/api/v1/deposits", async (CreateDepositRequest request, HttpRequest httpRequest, IDepositService deposits, CancellationToken cancellationToken) =>
{
    if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1
        || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 128 || keys[0] != keys[0]!.Trim())
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Informe uma chave única de 1 a 128 caracteres, sem espaços nas extremidades."] });
    }
    if (request.AccountId == Guid.Empty || request.AmountMinor <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["deposit"] = ["Informe uma conta e um valor positivo em centavos."] });
    }

    var result = await deposits.CreateAsync(request.AccountId, request.AmountMinor, keys[0]!, cancellationToken);
    return result.Outcome switch
    {
        DepositOutcome.Completed => Results.Created($"/api/v1/deposits/{result.Deposit!.Id}", result.Deposit),
        DepositOutcome.AccountNotFound => Results.NotFound(new { message = "Conta não encontrada." }),
        DepositOutcome.IdempotencyConflict => Results.Conflict(new { message = "A chave já foi utilizada com outros dados." }),
        _ => throw new InvalidOperationException("Resultado de depósito desconhecido.")
    };
});

app.MapGet("/api/v1/deposits/{depositId:guid}", async (Guid depositId, IDepositService deposits, CancellationToken cancellationToken) =>
{
    var deposit = await deposits.FindAsync(depositId, cancellationToken);
    return deposit is null ? Results.NotFound() : Results.Ok(deposit);
});

app.MapPost("/api/v1/pix/keys", async (CreatePixKeyRequest request, IPixService pix, CancellationToken cancellationToken) =>
{
    if (request.AccountId == Guid.Empty)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["accountId"] = ["Informe uma conta."] });
    var key = await pix.CreateKeyAsync(request.AccountId, cancellationToken);
    return key is null ? Results.NotFound(new { message = "Conta não encontrada." }) : Results.Created($"/api/v1/pix/keys/{key}", new PixKeyResult(key.Value, request.AccountId));
});

app.MapGet("/api/v1/pix/keys/{id:guid}", async (Guid id, IPixService pix, CancellationToken cancellationToken) =>
{
    var key = await pix.FindKeyAsync(id, cancellationToken);
    return key is null ? Results.NotFound() : Results.Ok(key);
});

app.MapPost("/api/v1/pix/operations", async (PixRequest request, HttpRequest httpRequest, IPixService pix, CancellationToken cancellationToken) =>
{
    if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1
        || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 128 || keys[0] != keys[0]!.Trim())
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Informe uma chave única de 1 a 128 caracteres, sem espaços nas extremidades."] });
    if (request.AccountId == Guid.Empty || request.KeyId == Guid.Empty || !PixRules.IsInstitution(request.InstitutionIspb)
        || request.Direction is not ("Outgoing" or "Incoming") || request.Scenario is not ("Confirm" or "Reject")
        || request.AmountMinor <= 0 || request.AmountMinor > PixRules.MaxAmountMinor)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pix"] = ["Informe conta, chave fictícia, instituição, direção, cenário e valor válidos."] });
    var response = await pix.CreateAsync(request, keys[0]!, cancellationToken);
    return response.Outcome switch
    {
        PixOutcome.Accepted => Results.Accepted($"/api/v1/pix/operations/{response.Operation!.Id}", response.Operation),
        PixOutcome.InsufficientFunds => Results.Conflict(response.Operation),
        PixOutcome.AccountNotFound or PixOutcome.KeyNotFound => Results.NotFound(new { message = "Conta ou chave fictícia não encontrada." }),
        PixOutcome.Conflict => Results.Conflict(new { message = "A chave de idempotência já foi utilizada com outros dados." }),
        _ => throw new InvalidOperationException("Resultado Pix desconhecido.")
    };
});

app.MapGet("/api/v1/pix/operations/{id:guid}", async (Guid id, IPixService pix, CancellationToken cancellationToken) =>
{
    var result = await pix.FindAsync(id, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapGet("/api/v1/pix/operations/{id:guid}/receipt", async (Guid id, IPixService pix, CancellationToken cancellationToken) =>
{
    var receipt = await pix.FindReceiptAsync(id, cancellationToken);
    return receipt is null ? Results.NotFound() : Results.Ok(receipt);
});

app.MapPost("/api/v1/pix/operations/{id:guid}/retry", async (Guid id, IPixService pix, CancellationToken cancellationToken) =>
    await pix.RetryAsync(id, cancellationToken)
        ? Results.Accepted($"/api/v1/pix/operations/{id}")
        : Results.Conflict(new { message = "Operação inexistente ou fora de revisão." }));

app.Run();

public sealed record CreateCustomerRequest(string? Name);
public sealed record OpenAccountRequest(Guid CustomerId);
public sealed record AccountResponse(Guid Id, Guid CustomerId, string Status, DateTimeOffset OpenedAt, long BalanceMinor, string Currency);
public sealed record CreateTransferRequest(Guid SourceAccountId, Guid DestinationAccountId, long AmountMinor);
public sealed record CreateDepositRequest(Guid AccountId, long AmountMinor);
public sealed record CreatePixKeyRequest(Guid AccountId);

public partial class Program
{
}
