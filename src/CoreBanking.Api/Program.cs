using CoreBanking.Application;
using CoreBanking.Application.Deposits;
using CoreBanking.Application.Transfers;
using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using CoreBanking.Infrastructure;
using CoreBanking.Infrastructure.Deposits;
using CoreBanking.Infrastructure.Transfers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<BankingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Banking")
        ?? throw new InvalidOperationException("Configure a conexão ConnectionStrings:Banking.")));
builder.Services.AddScoped<IBankingRepository, BankingRepository>();
builder.Services.AddScoped<ITransferService, TransferService>();
builder.Services.AddScoped<IDepositService, DepositService>();

var app = builder.Build();

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

app.Run();

public sealed record CreateCustomerRequest(string? Name);
public sealed record OpenAccountRequest(Guid CustomerId);
public sealed record AccountResponse(Guid Id, Guid CustomerId, string Status, DateTimeOffset OpenedAt, long BalanceMinor, string Currency);
public sealed record CreateTransferRequest(Guid SourceAccountId, Guid DestinationAccountId, long AmountMinor);
public sealed record CreateDepositRequest(Guid AccountId, long AmountMinor);

public partial class Program
{
}
