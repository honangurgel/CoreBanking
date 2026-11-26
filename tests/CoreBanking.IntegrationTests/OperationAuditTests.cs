using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using CoreBanking.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoreBanking.IntegrationTests;

public sealed class OperationAuditTests : IAsyncLifetime
{
    private const string Issuer = "https://issuer.example/realms/corebanking";
    private const string Audience = "corebanking-api";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await postgres.DisposeAsync();
    private BankingDbContext CreateContext() => new(new DbContextOptionsBuilder<BankingDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);

    private WebApplicationFactory<Program> CreateLocalApi() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
        builder.UseTestLoopback();
    });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static Task<List<AuditEvent>> EventsAsync(BankingDbContext db) =>
        db.Database.SqlQueryRaw<AuditEvent>("""
            SELECT "Action", "Outcome", "OperationId", "ActorKind", "Issuer", "Subject", "RequestId"
            FROM "OperationAuditEvents" ORDER BY "OccurredAt", "Id" LIMIT 100
            """).ToListAsync();

    [Fact]
    public async Task PersistedOperations_AreAuditedOnce_AndRecordsAreProtected()
    {
        using var api = CreateLocalApi();
        using var client = api.CreateClient();
        var source = await LoadTestHelpers.CreateAccountAsync(client);
        var destination = await LoadTestHelpers.CreateAccountAsync(client);
        var deposit = new { accountId = source, amountMinor = 1000 };
        var transfer = new { sourceAccountId = source, destinationAccountId = destination, amountMinor = 200 };
        using var createdDeposit = await PostAsync(client, "/api/v1/deposits", "audit-deposit", deposit);
        using var replayedDeposit = await PostAsync(client, "/api/v1/deposits", "audit-deposit", deposit);
        using var createdTransfer = await PostAsync(client, "/api/v1/transfers", "audit-transfer", transfer);
        using var replayedTransfer = await PostAsync(client, "/api/v1/transfers", "audit-transfer", transfer);
        using var rejectedTransfer = await PostAsync(client, "/api/v1/transfers", "audit-rejected",
            new { sourceAccountId = source, destinationAccountId = destination, amountMinor = 9999 });
        Assert.Equal(HttpStatusCode.Created, createdDeposit.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayedDeposit.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createdTransfer.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayedTransfer.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejectedTransfer.StatusCode);

        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = source });
        keyResponse.EnsureSuccessStatusCode();
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        var incoming = new { accountId = source, keyId = key, institutionIspb = "00000000",
            direction = "Incoming", amountMinor = 25, scenario = "Confirm" };
        using var createdPix = await PostAsync(client, "/api/v1/pix/operations", "audit-pix", incoming);
        using var replayedPix = await PostAsync(client, "/api/v1/pix/operations", "audit-pix", incoming);
        using var rejectedPix = await PostAsync(client, "/api/v1/pix/operations", "audit-pix-rejected",
            new { accountId = source, keyId = Guid.NewGuid(), institutionIspb = "00000000",
                direction = "Outgoing", amountMinor = 9999, scenario = "Confirm" });
        Assert.Equal(HttpStatusCode.Accepted, createdPix.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replayedPix.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejectedPix.StatusCode);

        await using var db = CreateContext();
        var events = await EventsAsync(db);
        var depositId = (await createdDeposit.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        Assert.Equal(5, events.Count);
        Assert.Single(events, item => item.Action == "Deposit" && item.Outcome == "Completed"
            && item.OperationId == depositId);
        Assert.Single(events, item => item.Action == "Transfer" && item.Outcome == "Completed");
        Assert.Single(events, item => item.Action == "Transfer" && item.Outcome == "InsufficientFunds");
        Assert.Single(events, item => item.Action == "PixCreate" && item.Outcome == "Pending");
        Assert.Single(events, item => item.Action == "PixCreate" && item.Outcome == "InsufficientFunds");
        Assert.All(events, item =>
        {
            Assert.Equal("LocalAnonymous", item.ActorKind);
            Assert.Null(item.Issuer);
            Assert.Null(item.Subject);
            Assert.False(string.IsNullOrWhiteSpace(item.RequestId));
        });
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""UPDATE "OperationAuditEvents" SET "Outcome" = 'Completed'"""));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""DELETE FROM "OperationAuditEvents" """));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""TRUNCATE "OperationAuditEvents" """));
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261126120000_AddStatementIndexes"));
        Assert.Equal(5, (await EventsAsync(db)).Count);
    }

    [Fact]
    public async Task OidcActor_IsTakenFromValidatedIssuerAndSubject_AndMissingSubjectIsRejected()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "audit-test" };
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
            builder.UseSetting("Authentication:Mode", "Oidc");
            builder.UseSetting("Authentication:Authority", Issuer);
            builder.UseSetting("Authentication:Audience", Audience);
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                    config.SigningKeys.Add(key);
                    options.Configuration = config;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                }));
        });
        using var client = api.CreateClient();
        string Token(string? subject)
        {
            var claims = new List<Claim>
            {
                new("roles", "operador"), new("roles", "financeiro"), new("roles", "administrador"), new("typ", "Bearer")
            };
            if (subject is not null) claims.Add(new Claim("sub", subject));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("synthetic-subject-1"));
        var account = await LoadTestHelpers.CreateAccountAsync(client);
        using var deposit = await PostAsync(client, "/api/v1/deposits", "oidc-audit", new { accountId = account, amountMinor = 1 });
        Assert.Equal(HttpStatusCode.Created, deposit.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(null));
        using var denied = await PostAsync(client, "/api/v1/deposits", "no-subject", new { accountId = account, amountMinor = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("synthetic-subject-1"));
        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = account });
        keyResponse.EnsureSuccessStatusCode();
        var pixKey = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        using var pixResponse = await PostAsync(client, "/api/v1/pix/operations", "oidc-pix",
            new { accountId = account, keyId = pixKey, institutionIspb = "00000000",
                direction = "Incoming", amountMinor = 1, scenario = "Confirm" });
        pixResponse.EnsureSuccessStatusCode();
        var pixId = (await pixResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        await using var db = CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"PixOperations\" SET \"Status\" = {"RequiresReview"} WHERE \"Id\" = {pixId}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("synthetic-subject-2"));
        using var access = await client.PostAsJsonAsync($"/api/v1/accounts/{account}/access/grant",
            new { issuer = Issuer, subject = "synthetic-subject-2" });
        Assert.Equal(HttpStatusCode.NoContent, access.StatusCode);
        using var retry = await client.PostAsync($"/api/v1/pix/operations/{pixId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

        var events = await EventsAsync(db);
        Assert.Equal(3, events.Count);
        var depositAudit = Assert.Single(events, item => item.Action == "Deposit");
        Assert.Equal("Oidc", depositAudit.ActorKind);
        Assert.Equal(Issuer, depositAudit.Issuer);
        Assert.Equal("synthetic-subject-1", depositAudit.Subject);
        var retryAudit = Assert.Single(events, item => item.Action == "PixRetry");
        Assert.Equal("Oidc", retryAudit.ActorKind);
        Assert.Equal(Issuer, retryAudit.Issuer);
        Assert.Equal("synthetic-subject-2", retryAudit.Subject);
        Assert.Equal(pixId, retryAudit.OperationId);
    }

    [Fact]
    public async Task AuditWriteFailure_RollsBackMovementsAndReprocessing()
    {
        using var api = CreateLocalApi();
        using var client = api.CreateClient();
        var source = await LoadTestHelpers.CreateAccountAsync(client);
        var destination = await LoadTestHelpers.CreateAccountAsync(client);
        using var seed = await PostAsync(client, "/api/v1/deposits", "seed", new { accountId = source, amountMinor = 1000 });
        seed.EnsureSuccessStatusCode();
        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = source });
        keyResponse.EnsureSuccessStatusCode();
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        var incoming = new { accountId = source, keyId = key, institutionIspb = "00000000",
            direction = "Incoming", amountMinor = 10, scenario = "Confirm" };
        using var pending = await PostAsync(client, "/api/v1/pix/operations", "pending", incoming);
        pending.EnsureSuccessStatusCode();
        var operation = (await pending.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        await using var db = CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"PixOperations\" SET \"Status\" = {"RequiresReview"} WHERE \"Id\" = {operation}");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_test_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected audit failure'; END; $$;
            CREATE TRIGGER reject_test_audit_trigger BEFORE INSERT ON "OperationAuditEvents"
            FOR EACH ROW EXECUTE FUNCTION reject_test_audit();
            """);

        using var failedDeposit = await PostAsync(client, "/api/v1/deposits", "failed-deposit", new { accountId = source, amountMinor = 10 });
        using var failedTransfer = await PostAsync(client, "/api/v1/transfers", "failed-transfer",
            new { sourceAccountId = source, destinationAccountId = destination, amountMinor = 20 });
        using var failedPix = await PostAsync(client, "/api/v1/pix/operations", "failed-pix", incoming);
        using var failedRetry = await client.PostAsync($"/api/v1/pix/operations/{operation}/retry", null);
        Assert.Equal(HttpStatusCode.InternalServerError, failedDeposit.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, failedTransfer.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, failedPix.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, failedRetry.StatusCode);
        Assert.Equal(1, await db.Deposits.CountAsync());
        Assert.Equal(0, await db.Transfers.CountAsync());
        Assert.Equal(1, await db.PixOperations.CountAsync());
        Assert.Equal("RequiresReview", (await db.PixOperations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, await db.PixTransitions.CountAsync());
        Assert.Equal(2, (await EventsAsync(db)).Count);
        Assert.Equal(1000, (await db.LedgerAccounts.AsNoTracking().SingleAsync(item => item.CustomerAccountId == source)).BalanceMinor);
        await LoadTestHelpers.AssertLedgerAsync(db);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record AuditEvent(string Action, string Outcome, Guid OperationId, string ActorKind,
        string? Issuer, string? Subject, string RequestId);
}
