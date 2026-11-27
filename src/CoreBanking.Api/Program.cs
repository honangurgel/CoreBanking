using CoreBanking.Application;
using CoreBanking.Application.Deposits;
using CoreBanking.Application.Cards;
using CoreBanking.Application.Pix;
using CoreBanking.Application.Transfers;
using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
using CoreBanking.Infrastructure;
using CoreBanking.Infrastructure.Deposits;
using CoreBanking.Infrastructure.Cards;
using CoreBanking.Infrastructure.Pix;
using CoreBanking.Infrastructure.Transfers;
using CoreBanking.Api;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Threading.RateLimiting;
using CoreBanking.Telemetry;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole(options => options.FormatterName = "json");

builder.Services.AddLocalTracing(builder.Configuration, "corebanking-api", tracing => tracing.AddAspNetCoreInstrumentation());

var authMode = builder.Configuration["Authentication:Mode"] ?? "Oidc";
if (authMode is not ("Oidc" or "Disabled"))
    throw new InvalidOperationException("Authentication:Mode deve ser Oidc ou Disabled.");
if (authMode == "Disabled" && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("A autenticação só pode ser desativada em Development.");
if (authMode == "Oidc")
{
    var authority = builder.Configuration["Authentication:Authority"];
    var audience = builder.Configuration["Authentication:Audience"];
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
        !string.IsNullOrEmpty(authorityUri.UserInfo) || !string.IsNullOrEmpty(authorityUri.Query) ||
        !string.IsNullOrEmpty(authorityUri.Fragment) ||
        (authorityUri.Scheme != Uri.UriSchemeHttps &&
         !(builder.Environment.IsDevelopment() && authorityUri.Scheme == Uri.UriSchemeHttp && authorityUri.IsLoopback)) ||
        string.IsNullOrWhiteSpace(audience))
        throw new InvalidOperationException("Configure Authentication:Authority HTTPS e Authentication:Audience; HTTP local é permitido apenas em Development.");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = authorityUri.Scheme == Uri.UriSchemeHttps;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
    });
    builder.Services.AddAuthorization(options =>
    {
        foreach (var role in new[] { "operador", "financeiro", "administrador" })
            options.AddPolicy(role, policy => policy.RequireAuthenticatedUser().RequireClaim("typ", "Bearer").RequireRole(role));
    });
}

builder.Services.AddOpenApi();
builder.Services.AddDbContext<BankingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Banking")
        ?? throw new InvalidOperationException("Configure a conexão ConnectionStrings:Banking.")));
builder.Services.AddScoped<IBankingRepository, BankingRepository>();
builder.Services.AddScoped<IAccountStatementService, AccountStatementService>();
builder.Services.AddScoped<IAccountAccess, AccountAccessService>();
builder.Services.AddScoped<ITransferService, TransferService>();
builder.Services.AddScoped<IDepositService, DepositService>();
builder.Services.AddScoped<IPixService, PixService>();
builder.Services.AddScoped<IDebitCardService, DebitCardService>();
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", timeout: TimeSpan.FromSeconds(10))
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", timeout: TimeSpan.FromSeconds(10))
    .AddCheck<SimulatorHealthCheck>("simulator", timeout: TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<SimulatorHealthCheck>(client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });

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

if (authMode == "Disabled")
{
    app.Use(async (context, next) =>
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next();
    });
}
app.UseRateLimiter();
if (authMode == "Oidc")
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var operators = app.MapGroup("/api/v1");
var finance = app.MapGroup("/api/v1");
var administrators = app.MapGroup("/api/v1");
async Task<bool> CanAccessAsync(HttpContext context, IAccountAccess access, Guid accountId, CancellationToken cancellationToken)
{
    if (authMode == "Disabled") return true;
    var actor = AuditActorResolver.Resolve(context, false);
    return actor is not null && await access.HasAccessAsync(actor, accountId, cancellationToken);
}
static string? IdempotencyKey(HttpRequest request)
{
    if (!request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1
        || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 128 || keys[0] != keys[0]!.Trim())
        return null;
    return keys[0];
}
static IResult InvalidIdempotencyKey() => Results.ValidationProblem(new Dictionary<string, string[]>
{
    ["Idempotency-Key"] = ["Informe uma chave única de 1 a 128 caracteres, sem espaços nas extremidades."]
});
if (authMode == "Oidc")
{
    operators.RequireAuthorization("operador");
    finance.RequireAuthorization("financeiro");
    administrators.RequireAuthorization("administrador");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => TypedResults.Ok(new { status = "Healthy" }))
    .WithName("Health");

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString())
        });
    }
});

operators.MapPost("/customers", async (CreateCustomerRequest request, IBankingRepository repository, CancellationToken cancellationToken) =>
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

operators.MapGet("/customers/{customerId:guid}", async (Guid customerId, IBankingRepository repository, CancellationToken cancellationToken) =>
{
    var customer = await repository.FindCustomerAsync(customerId, cancellationToken);
    return customer is null ? Results.NotFound() : Results.Ok(customer);
});

operators.MapPost("/accounts", async (OpenAccountRequest request, HttpContext context, IBankingRepository repository, CancellationToken cancellationToken) =>
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
    var actor = AuditActorResolver.Resolve(context, authMode == "Disabled");
    if (actor is null) return Results.Forbid();
    await repository.AddAccountAsync(account, actor, cancellationToken);
    return Results.Created($"/api/v1/accounts/{account.Id}", new AccountResponse(account.Id, account.CustomerId, account.Status.ToString(), account.OpenedAt, 0, "BRL"));
});

operators.MapGet("/accounts/{accountId:guid}", async (Guid accountId, HttpContext context, IAccountAccess access, IBankingRepository repository, CancellationToken cancellationToken) =>
{
    if (!await CanAccessAsync(context, access, accountId, cancellationToken)) return Results.NotFound();
    var details = await repository.FindAccountAsync(accountId, cancellationToken);
    return details is null
        ? Results.NotFound()
        : Results.Ok(new AccountResponse(details.Account.Id, details.Account.CustomerId, details.Account.Status.ToString(), details.Account.OpenedAt, details.BalanceMinor, "BRL"));
});

operators.MapGet("/accounts/{accountId:guid}/statement", async (
    Guid accountId, int? pageSize, DateTimeOffset? beforeCreatedAt, Guid? beforeEntryId,
    HttpContext context, IAccountAccess access, IAccountStatementService statements, CancellationToken cancellationToken) =>
{
    var size = pageSize ?? 50;
    if (size is < 1 or > 100)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pageSize"] = ["Informe um tamanho de página entre 1 e 100."] });
    if (beforeCreatedAt.HasValue != beforeEntryId.HasValue || beforeEntryId == Guid.Empty)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Informe beforeCreatedAt e beforeEntryId juntos, com identificador não vazio."] });

    if (!await CanAccessAsync(context, access, accountId, cancellationToken)) return Results.NotFound();
    var cursor = beforeCreatedAt is { } time && beforeEntryId is { } id ? new StatementCursor(time, id) : null;
    var statement = await statements.FindAsync(accountId, size, cursor, cancellationToken);
    return statement is null ? Results.NotFound() : Results.Ok(statement);
});

finance.MapPost("/transfers", async (CreateTransferRequest request, HttpRequest httpRequest, IAccountAccess access, ITransferService transfers, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var key = IdempotencyKey(httpRequest);
    if (key is null) return InvalidIdempotencyKey();

    if (request.SourceAccountId == Guid.Empty || request.DestinationAccountId == Guid.Empty
        || request.SourceAccountId == request.DestinationAccountId || request.AmountMinor <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["transfer"] = ["Informe contas distintas e um valor positivo em centavos."] });
    }

    var actor = AuditActorResolver.Resolve(httpRequest.HttpContext, authMode == "Disabled");
    if (actor is null) return Results.Forbid();
    if (!await CanAccessAsync(httpRequest.HttpContext, access, request.SourceAccountId, cancellationToken)
        || !await CanAccessAsync(httpRequest.HttpContext, access, request.DestinationAccountId, cancellationToken))
        return Results.NotFound();
    var result = await transfers.CreateAsync(request.SourceAccountId, request.DestinationAccountId, request.AmountMinor, key, actor, cancellationToken);
    if (result.Transfer is { } transfer)
        logger.LogInformation("Transferência {TransferId} retornou {Status} para requisição {RequestId}.",
            transfer.Id, transfer.Status, httpRequest.HttpContext.TraceIdentifier);
    return result.Outcome switch
    {
        TransferOutcome.Completed => Results.Created($"/api/v1/transfers/{result.Transfer!.Id}", result.Transfer),
        TransferOutcome.InsufficientFunds => Results.Conflict(result.Transfer),
        TransferOutcome.AccountNotFound => Results.NotFound(new { message = "Conta não encontrada." }),
        TransferOutcome.IdempotencyConflict => Results.Conflict(new { message = "A chave já foi utilizada com outros dados." }),
        _ => throw new InvalidOperationException("Resultado de transferência desconhecido.")
    };
});

operators.MapGet("/transfers/{transferId:guid}", async (Guid transferId, HttpContext context, IAccountAccess access, ITransferService transfers, CancellationToken cancellationToken) =>
{
    var transfer = await transfers.FindAsync(transferId, cancellationToken);
    if (transfer is null || !await CanAccessAsync(context, access, transfer.SourceAccountId, cancellationToken)
        || !await CanAccessAsync(context, access, transfer.DestinationAccountId, cancellationToken)) return Results.NotFound();
    return Results.Ok(transfer);
});

finance.MapPost("/deposits", async (CreateDepositRequest request, HttpRequest httpRequest, IAccountAccess access, IDepositService deposits, CancellationToken cancellationToken) =>
{
    var key = IdempotencyKey(httpRequest);
    if (key is null) return InvalidIdempotencyKey();
    if (request.AccountId == Guid.Empty || request.AmountMinor <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["deposit"] = ["Informe uma conta e um valor positivo em centavos."] });
    }

    var actor = AuditActorResolver.Resolve(httpRequest.HttpContext, authMode == "Disabled");
    if (actor is null) return Results.Forbid();
    if (!await CanAccessAsync(httpRequest.HttpContext, access, request.AccountId, cancellationToken)) return Results.NotFound();
    var result = await deposits.CreateAsync(request.AccountId, request.AmountMinor, key, actor, cancellationToken);
    return result.Outcome switch
    {
        DepositOutcome.Completed => Results.Created($"/api/v1/deposits/{result.Deposit!.Id}", result.Deposit),
        DepositOutcome.AccountNotFound => Results.NotFound(new { message = "Conta não encontrada." }),
        DepositOutcome.IdempotencyConflict => Results.Conflict(new { message = "A chave já foi utilizada com outros dados." }),
        _ => throw new InvalidOperationException("Resultado de depósito desconhecido.")
    };
});

operators.MapGet("/deposits/{depositId:guid}", async (Guid depositId, HttpContext context, IAccountAccess access, IDepositService deposits, CancellationToken cancellationToken) =>
{
    var deposit = await deposits.FindAsync(depositId, cancellationToken);
    if (deposit is null || !await CanAccessAsync(context, access, deposit.AccountId, cancellationToken)) return Results.NotFound();
    return Results.Ok(deposit);
});

operators.MapPost("/pix/keys", async (CreatePixKeyRequest request, HttpContext context, IAccountAccess access, IPixService pix, CancellationToken cancellationToken) =>
{
    if (request.AccountId == Guid.Empty)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["accountId"] = ["Informe uma conta."] });
    if (!await CanAccessAsync(context, access, request.AccountId, cancellationToken)) return Results.NotFound();
    var key = await pix.CreateKeyAsync(request.AccountId, cancellationToken);
    return key is null ? Results.NotFound(new { message = "Conta não encontrada." }) : Results.Created($"/api/v1/pix/keys/{key}", new PixKeyResult(key.Value, request.AccountId));
});

operators.MapGet("/pix/keys/{id:guid}", async (Guid id, HttpContext context, IAccountAccess access, IPixService pix, CancellationToken cancellationToken) =>
{
    var key = await pix.FindKeyAsync(id, cancellationToken);
    if (key is null || !await CanAccessAsync(context, access, key.AccountId, cancellationToken)) return Results.NotFound();
    return Results.Ok(key);
});

finance.MapPost("/pix/operations", async (PixRequest request, HttpRequest httpRequest, IAccountAccess access, IPixService pix, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var key = IdempotencyKey(httpRequest);
    if (key is null) return InvalidIdempotencyKey();
    if (request.AccountId == Guid.Empty || request.KeyId == Guid.Empty || !PixRules.IsInstitution(request.InstitutionIspb)
        || request.Direction is not ("Outgoing" or "Incoming") || request.Scenario is not ("Confirm" or "Reject")
        || request.AmountMinor <= 0 || request.AmountMinor > PixRules.MaxAmountMinor)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pix"] = ["Informe conta, chave fictícia, instituição, direção, cenário e valor válidos."] });
    var actor = AuditActorResolver.Resolve(httpRequest.HttpContext, authMode == "Disabled");
    if (actor is null) return Results.Forbid();
    if (!await CanAccessAsync(httpRequest.HttpContext, access, request.AccountId, cancellationToken)) return Results.NotFound();
    var response = await pix.CreateAsync(request, key, actor, cancellationToken);
    if (response.Operation is { } operation)
        logger.LogInformation("Pix {OperationId} retornou {Status} para requisição {RequestId}.",
            operation.Id, operation.Status, httpRequest.HttpContext.TraceIdentifier);
    return response.Outcome switch
    {
        PixOutcome.Accepted => Results.Accepted($"/api/v1/pix/operations/{response.Operation!.Id}", response.Operation),
        PixOutcome.InsufficientFunds => Results.Conflict(response.Operation),
        PixOutcome.AccountNotFound or PixOutcome.KeyNotFound => Results.NotFound(new { message = "Conta ou chave fictícia não encontrada." }),
        PixOutcome.Conflict => Results.Conflict(new { message = "A chave de idempotência já foi utilizada com outros dados." }),
        _ => throw new InvalidOperationException("Resultado Pix desconhecido.")
    };
});

operators.MapGet("/pix/operations/{id:guid}", async (Guid id, HttpContext context, IAccountAccess access, IPixService pix, CancellationToken cancellationToken) =>
{
    var result = await pix.FindAsync(id, cancellationToken);
    if (result is null || !await CanAccessAsync(context, access, result.AccountId, cancellationToken)) return Results.NotFound();
    return Results.Ok(result);
});

operators.MapGet("/pix/operations/{id:guid}/receipt", async (Guid id, HttpContext context, IAccountAccess access, IPixService pix, CancellationToken cancellationToken) =>
{
    var receipt = await pix.FindReceiptAsync(id, cancellationToken);
    if (receipt is null || !await CanAccessAsync(context, access, receipt.Operation.AccountId, cancellationToken)) return Results.NotFound();
    return Results.Ok(receipt);
});

administrators.MapPost("/pix/operations/{id:guid}/retry", async (Guid id, HttpRequest httpRequest, IAccountAccess access, IPixService pix, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var actor = AuditActorResolver.Resolve(httpRequest.HttpContext, authMode == "Disabled");
    if (actor is null) return Results.Forbid();
    var operation = await pix.FindAsync(id, cancellationToken);
    if (authMode != "Disabled" && (operation is null ||
        !await CanAccessAsync(httpRequest.HttpContext, access, operation.AccountId, cancellationToken))) return Results.NotFound();
    var retried = await pix.RetryAsync(id, actor, cancellationToken);
    logger.LogInformation("Reprocessamento do Pix {OperationId}: {RetryAccepted}, requisição {RequestId}.",
        id, retried, httpRequest.HttpContext.TraceIdentifier);
    return retried
        ? Results.Accepted($"/api/v1/pix/operations/{id}")
        : Results.Conflict(new { message = "Operação inexistente ou fora de revisão." });
});

finance.MapPost("/debit-cards", async (CreateDebitCardRequest request, HttpRequest httpRequest, IAccountAccess access,
    IDebitCardService cards, CancellationToken cancellationToken) =>
{
    var key = IdempotencyKey(httpRequest);
    if (key is null) return InvalidIdempotencyKey();
    if (request.AccountId == Guid.Empty)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["accountId"] = ["Informe uma conta."] });
    var actor = AuditActorResolver.Resolve(httpRequest.HttpContext, authMode == "Disabled");
    if (actor is null) return Results.Forbid();
    if (!await CanAccessAsync(httpRequest.HttpContext, access, request.AccountId, cancellationToken)) return Results.NotFound();
    var response = await cards.IssueAsync(request.AccountId, key, actor, cancellationToken);
    return response.Outcome switch
    {
        DebitCardOutcome.Created => Results.Created($"/api/v1/debit-cards/{response.Card!.Id}", response.Card),
        DebitCardOutcome.NotFound => Results.NotFound(),
        _ => throw new InvalidOperationException("Resultado de emissão de cartão desconhecido.")
    };
});

operators.MapGet("/debit-cards/{id:guid}", async (Guid id, HttpContext context, IAccountAccess access,
    IDebitCardService cards, CancellationToken cancellationToken) =>
{
    var card = await cards.FindCardAsync(id, cancellationToken);
    if (card is null || !await CanAccessAsync(context, access, card.AccountId, cancellationToken)) return Results.NotFound();
    return Results.Ok(card);
});

finance.MapPost("/debit-cards/{id:guid}/authorizations", async (Guid id, DebitAuthorizationRequest request,
    HttpRequest httpRequest, IAccountAccess access, IDebitCardService cards, CancellationToken cancellationToken) =>
{
    var key = IdempotencyKey(httpRequest);
    if (key is null) return InvalidIdempotencyKey();
    if (request.AmountMinor <= 0 || request.AmountMinor > DebitCardRules.MaxAmountMinor)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["amountMinor"] = ["Informe um valor válido e positivo em centavos."] });
    var actor = AuditActorResolver.Resolve(httpRequest.HttpContext, authMode == "Disabled");
    if (actor is null) return Results.Forbid();
    var card = await cards.FindCardAsync(id, cancellationToken);
    if (card is null || !await CanAccessAsync(httpRequest.HttpContext, access, card.AccountId, cancellationToken)) return Results.NotFound();
    var response = await cards.AuthorizeAsync(id, request.AmountMinor, key, actor, cancellationToken);
    return response.Outcome switch
    {
        DebitCardOutcome.Authorized => Results.Created($"/api/v1/debit-authorizations/{response.Authorization!.Id}", response.Authorization),
        DebitCardOutcome.InsufficientFunds => Results.Conflict(response.Authorization),
        DebitCardOutcome.Conflict => Results.Conflict(new { message = "A chave de idempotência já foi utilizada com outros dados." }),
        DebitCardOutcome.NotFound => Results.NotFound(),
        _ => throw new InvalidOperationException("Resultado de autorização de cartão desconhecido.")
    };
});

operators.MapGet("/debit-authorizations/{id:guid}", async (Guid id, HttpContext context, IAccountAccess access,
    IDebitCardService cards, CancellationToken cancellationToken) =>
{
    var authorization = await cards.FindAuthorizationAsync(id, cancellationToken);
    if (authorization is null || !await CanAccessAsync(context, access, authorization.AccountId, cancellationToken)) return Results.NotFound();
    return Results.Ok(authorization);
});

finance.MapPost("/debit-authorizations/{id:guid}/{action}", async (Guid id, string action, HttpRequest httpRequest,
    IAccountAccess access, IDebitCardService cards, CancellationToken cancellationToken) =>
{
    if (action is not ("settle" or "cancel")) return Results.NotFound();
    if (httpRequest.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["A finalização não aceita corpo; a liquidação é sempre integral."] });
    var actor = AuditActorResolver.Resolve(httpRequest.HttpContext, authMode == "Disabled");
    if (actor is null) return Results.Forbid();
    var authorization = await cards.FindAuthorizationAsync(id, cancellationToken);
    if (authorization is null || !await CanAccessAsync(httpRequest.HttpContext, access, authorization.AccountId, cancellationToken)) return Results.NotFound();
    var response = await cards.FinalizeAsync(id, action == "settle", actor, cancellationToken);
    return response.Outcome switch
    {
        DebitCardOutcome.Settled or DebitCardOutcome.Cancelled => Results.Ok(response.Authorization),
        DebitCardOutcome.Conflict => Results.Conflict(new { message = "Autorização fora do estado permitido." }),
        DebitCardOutcome.NotFound => Results.NotFound(),
        _ => throw new InvalidOperationException("Resultado de finalização de cartão desconhecido.")
    };
});

administrators.MapPost("/accounts/{accountId:guid}/access/{action}", async (
    Guid accountId, string action, AccountAccessRequest request, HttpContext context,
    IAccountAccess access, CancellationToken cancellationToken) =>
{
    if (authMode == "Disabled") return Results.NotFound();
    var actor = AuditActorResolver.Resolve(context, false);
    if (actor is null) return Results.Forbid();
    if (action is not ("grant" or "revoke")) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Issuer) || request.Issuer.Length > 512 ||
        request.Issuer != request.Issuer.Trim() || string.IsNullOrWhiteSpace(request.Subject) ||
        request.Subject.Length > 256 || request.Subject != request.Subject.Trim())
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["identity"] = ["Informe emissor e identificador válidos."] });
    return await access.ChangeAsync(actor, accountId, request.Issuer, request.Subject, action == "grant", cancellationToken)
        ? Results.NoContent() : Results.NotFound();
});

app.Run();

public sealed record CreateCustomerRequest(string? Name);
public sealed record OpenAccountRequest(Guid CustomerId);
public sealed record AccountResponse(Guid Id, Guid CustomerId, string Status, DateTimeOffset OpenedAt, long BalanceMinor, string Currency);
public sealed record CreateTransferRequest(Guid SourceAccountId, Guid DestinationAccountId, long AmountMinor);
public sealed record CreateDepositRequest(Guid AccountId, long AmountMinor);
public sealed record CreatePixKeyRequest(Guid AccountId);
public sealed record CreateDebitCardRequest(Guid AccountId);
public sealed record DebitAuthorizationRequest(long AmountMinor);
public sealed record AccountAccessRequest(string? Issuer, string? Subject);

public partial class Program
{
}
