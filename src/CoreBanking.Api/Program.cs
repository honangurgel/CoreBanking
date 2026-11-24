using CoreBanking.Application;
using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<BankingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Banking")
        ?? throw new InvalidOperationException("Configure a conexão ConnectionStrings:Banking.")));
builder.Services.AddScoped<IBankingRepository, BankingRepository>();

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
    return Results.Created($"/api/v1/accounts/{account.Id}", new AccountResponse(account.Id, account.CustomerId, account.Status.ToString(), account.OpenedAt));
});

app.MapGet("/api/v1/accounts/{accountId:guid}", async (Guid accountId, IBankingRepository repository, CancellationToken cancellationToken) =>
{
    var account = await repository.FindAccountAsync(accountId, cancellationToken);
    return account is null
        ? Results.NotFound()
        : Results.Ok(new AccountResponse(account.Id, account.CustomerId, account.Status.ToString(), account.OpenedAt));
});

app.Run();

public sealed record CreateCustomerRequest(string? Name);
public sealed record OpenAccountRequest(Guid CustomerId);
public sealed record AccountResponse(Guid Id, Guid CustomerId, string Status, DateTimeOffset OpenedAt);

public partial class Program
{
}
