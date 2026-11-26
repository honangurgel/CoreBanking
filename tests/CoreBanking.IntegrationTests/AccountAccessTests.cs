using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Diagnostics;
using CoreBanking.Application;
using CoreBanking.Domain.Accounts;
using CoreBanking.Domain.Customers;
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

public sealed class AccountAccessTests : IAsyncLifetime
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

    [Fact]
    public async Task ExplicitAssignments_ProtectOperationsAndAreAudited()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "access-test" };
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
        HttpClient Client(string subject, params string[] roles)
        {
            var claims = roles.Select(role => new Claim("roles", role)).ToList();
            claims.Add(new Claim("sub", subject));
            claims.Add(new Claim("typ", "Bearer"));
            var jwt = new JwtSecurityToken(Issuer, Audience, claims, expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            var client = api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            return client;
        }
        using var alice = Client("alice", "operador", "financeiro", "administrador");
        using var bob = Client("bob", "operador", "financeiro");
        using var admin = Client("admin", "administrador");
        var a = await LoadTestHelpers.CreateAccountAsync(alice);
        var b = await LoadTestHelpers.CreateAccountAsync(bob);

        async Task<HttpStatusCode> GrantAsync(HttpClient client, Guid account, string subject, string action) =>
            (await client.PostAsJsonAsync($"/api/v1/accounts/{account}/access/{action}", new { issuer = Issuer, subject })).StatusCode;
        async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string idempotencyKey, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            return await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/accounts/{a}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/accounts/{b}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/accounts/{b}/statement")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await GrantAsync(bob, b, "alice", "grant"));
        using var deposit = await PostAsync(alice, "/api/v1/deposits", "access-deposit", new { accountId = a, amountMinor = 100 });
        Assert.Equal(HttpStatusCode.Created, deposit.StatusCode);
        var depositId = (await deposit.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        using var bobDeposit = await PostAsync(bob, "/api/v1/deposits", "access-deposit", new { accountId = b, amountMinor = 50 });
        Assert.Equal(HttpStatusCode.Created, bobDeposit.StatusCode);
        Assert.NotEqual(depositId, (await bobDeposit.Content.ReadFromJsonAsync<IdResponse>())!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/deposits/{depositId}")).StatusCode);
        using var forbiddenDeposit = await PostAsync(bob, "/api/v1/deposits", "other-deposit", new { accountId = a, amountMinor = 1 });
        Assert.Equal(HttpStatusCode.NotFound, forbiddenDeposit.StatusCode);
        var transfer = new { sourceAccountId = a, destinationAccountId = b, amountMinor = 25 };
        using var blockedTransfer = await PostAsync(alice, "/api/v1/transfers", "access-transfer", transfer);
        Assert.Equal(HttpStatusCode.NotFound, blockedTransfer.StatusCode);

        using var pixKeyResponse = await bob.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = b });
        pixKeyResponse.EnsureSuccessStatusCode();
        var pixKey = (await pixKeyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/pix/keys/{pixKey}")).StatusCode);
        using var deniedKey = await alice.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = b });
        Assert.Equal(HttpStatusCode.NotFound, deniedKey.StatusCode);
        var pix = new { accountId = b, keyId = pixKey, institutionIspb = "00000000",
            direction = "Incoming", amountMinor = 1, scenario = "Confirm" };
        using var deniedPix = await PostAsync(alice, "/api/v1/pix/operations", "blocked-pix", pix);
        Assert.Equal(HttpStatusCode.NotFound, deniedPix.StatusCode);
        using var createdPix = await PostAsync(bob, "/api/v1/pix/operations", "bob-pix", pix);
        createdPix.EnsureSuccessStatusCode();
        var pixId = (await createdPix.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/pix/operations/{pixId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/pix/operations/{pixId}/receipt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync($"/api/v1/pix/operations/{pixId}/retry", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, await GrantAsync(admin, b, "alice", "grant"));
        Assert.Equal(HttpStatusCode.NoContent, await GrantAsync(admin, b, "alice", "grant"));
        using var sent = await PostAsync(alice, "/api/v1/transfers", "access-transfer", transfer);
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var transferId = (await sent.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/transfers/{transferId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/transfers/{transferId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/pix/operations/{pixId}/receipt")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, await GrantAsync(admin, b, "alice", "revoke"));
        Assert.Equal(HttpStatusCode.NoContent, await GrantAsync(admin, b, "alice", "revoke"));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/transfers/{transferId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/accounts/{b}")).StatusCode);
        using var replay = await PostAsync(alice, "/api/v1/transfers", "access-transfer", transfer);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/v1/accounts/{b}")).StatusCode);

        await using var db = CreateContext();
        var audit = await db.Database.SqlQueryRaw<AccessAudit>("""
            SELECT "Action", "AccountId", "TargetSubject", "ActorSubject"
            FROM "AccountAccessAudit" ORDER BY "OccurredAt", "Id"
            """).ToListAsync();
        Assert.Equal(4, audit.Count);
        Assert.Single(audit, item => item.AccountId == a && item.Action == "Grant" && item.ActorSubject == "alice");
        Assert.Single(audit, item => item.AccountId == b && item.Action == "Grant" && item.ActorSubject == "bob");
        Assert.Single(audit, item => item.AccountId == b && item.Action == "Grant" && item.ActorSubject == "admin" && item.TargetSubject == "alice");
        Assert.Single(audit, item => item.AccountId == b && item.Action == "Revoke" && item.ActorSubject == "admin" && item.TargetSubject == "alice");
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""UPDATE "AccountAccessAudit" SET "Action" = 'Grant'"""));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""DELETE FROM "AccountAccessAudit" """));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""TRUNCATE "AccountAccessAudit" """));
        await LoadTestHelpers.AssertLedgerAsync(db);
    }

    [Fact]
    public async Task Revocation_WaitsForAuthorizedRequest_ThenDeniesFurtherAccess()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
            builder.UseTestLoopback();
        });
        using var client = api.CreateClient();
        var account = await LoadTestHelpers.CreateAccountAsync(client);
        var actor = AuditActor.Oidc(Issuer, "alice", "request");
        await using var grantDb = CreateContext();
        await using var grantService = new AccountAccessService(grantDb);
        Assert.True(await grantService.ChangeAsync(actor, account, Issuer, "alice", true, CancellationToken.None));

        await using var requestDb = CreateContext();
        var requestAccess = new AccountAccessService(requestDb);
        await using var revokeDb = CreateContext();
        await revokeDb.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)revokeDb.Database.GetDbConnection()).ProcessID;
        await using var revokeService = new AccountAccessService(revokeDb);
        try
        {
            Assert.True(await requestAccess.HasAccessAsync(actor, account, CancellationToken.None));
            var revoke = revokeService.ChangeAsync(actor, account, Issuer, "alice", false, CancellationToken.None);
            await using var observer = CreateContext();
            var timer = Stopwatch.StartNew();
            var blocked = false;
            while (timer.Elapsed < TimeSpan.FromSeconds(10) && !revoke.IsCompleted)
            {
                blocked = await observer.Database.SqlQuery<bool>($"""
                    SELECT cardinality(pg_blocking_pids({pid})) > 0 AS "Value"
                    """).SingleAsync();
                if (blocked) break;
                await Task.Delay(50);
            }
            Assert.True(blocked, "A revogação deveria aguardar a requisição autorizada.");
            await requestAccess.DisposeAsync();
            Assert.True(await revoke.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await requestAccess.DisposeAsync();
        }
        await using var nextDb = CreateContext();
        await using var nextRequest = new AccountAccessService(nextDb);
        Assert.False(await nextRequest.HasAccessAsync(actor, account, CancellationToken.None));
    }

    [Fact]
    public async Task AccessAuditFailure_RollsBackGrantAndRevocation()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
            builder.UseTestLoopback();
        });
        using var client = api.CreateClient();
        var account = await LoadTestHelpers.CreateAccountAsync(client);
        var actor = AuditActor.Oidc(Issuer, "admin", "request");
        await using var db = CreateContext();
        await using var access = new AccountAccessService(db);
        Assert.True(await access.ChangeAsync(actor, account, Issuer, "alice", true, CancellationToken.None));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_access_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected access audit failure'; END; $$;
            CREATE TRIGGER reject_access_audit_trigger BEFORE INSERT ON "AccountAccessAudit"
            FOR EACH ROW EXECUTE FUNCTION reject_access_audit();
            """);
        await Assert.ThrowsAsync<PostgresException>(() =>
            access.ChangeAsync(actor, account, Issuer, "bob", true, CancellationToken.None));
        await Assert.ThrowsAsync<PostgresException>(() =>
            access.ChangeAsync(actor, account, Issuer, "alice", false, CancellationToken.None));
        var subjects = await db.Database.SqlQuery<string>($"""
            SELECT "Subject" AS "Value" FROM "AccountAccessGrants" WHERE "AccountId" = {account}
            """).ToListAsync();
        Assert.Equal(["alice"], subjects);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            """SELECT count(*)::int AS "Value" FROM "AccountAccessAudit" """).SingleAsync());
    }

    [Fact]
    public async Task CreatorGrantAuditFailure_RollsBackAccountAndLedger()
    {
        await using var db = CreateContext();
        var repository = new BankingRepository(db);
        var customer = Customer.Create("Cliente fictício");
        await repository.AddCustomerAsync(customer, CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_access_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected access audit failure'; END; $$;
            CREATE TRIGGER reject_access_audit_trigger BEFORE INSERT ON "AccountAccessAudit"
            FOR EACH ROW EXECUTE FUNCTION reject_access_audit();
            """);
        var account = Account.Open(customer.Id);
        var actor = AuditActor.Oidc(Issuer, "alice", "request");
        await Assert.ThrowsAsync<PostgresException>(() =>
            repository.AddAccountAsync(account, actor, CancellationToken.None));
        await using var verify = CreateContext();
        Assert.False(await verify.Accounts.AnyAsync(item => item.Id == account.Id));
        Assert.False(await verify.LedgerAccounts.AnyAsync(item => item.CustomerAccountId == account.Id));
    }

    [Fact]
    public async Task Idempotency_IsScopedByAccountOrTransferPair_AndDowngradeRejectsDuplicates()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
            builder.UseTestLoopback();
        });
        using var client = api.CreateClient();
        var a = await LoadTestHelpers.CreateAccountAsync(client);
        var b = await LoadTestHelpers.CreateAccountAsync(client);
        var c = await LoadTestHelpers.CreateAccountAsync(client);
        async Task<HttpResponseMessage> PostAsync(string path, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", "shared-key");
            return await client.SendAsync(request);
        }

        using var depositA = await PostAsync("/api/v1/deposits", new { accountId = a, amountMinor = 100 });
        using var depositB = await PostAsync("/api/v1/deposits", new { accountId = b, amountMinor = 200 });
        using var depositReplay = await PostAsync("/api/v1/deposits", new { accountId = a, amountMinor = 100 });
        using var depositConflict = await PostAsync("/api/v1/deposits", new { accountId = a, amountMinor = 101 });
        Assert.Equal(HttpStatusCode.Created, depositA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, depositB.StatusCode);
        Assert.Equal((await depositA.Content.ReadFromJsonAsync<IdResponse>())!.Id,
            (await depositReplay.Content.ReadFromJsonAsync<IdResponse>())!.Id);
        Assert.Equal(HttpStatusCode.Conflict, depositConflict.StatusCode);

        var transfer = new { sourceAccountId = a, destinationAccountId = b, amountMinor = 10 };
        using var transferAB = await PostAsync("/api/v1/transfers", transfer);
        using var transferAC = await PostAsync("/api/v1/transfers",
            new { sourceAccountId = a, destinationAccountId = c, amountMinor = 10 });
        using var transferReplay = await PostAsync("/api/v1/transfers", transfer);
        using var transferConflict = await PostAsync("/api/v1/transfers",
            new { sourceAccountId = a, destinationAccountId = b, amountMinor = 11 });
        Assert.Equal(HttpStatusCode.Created, transferAB.StatusCode);
        Assert.Equal(HttpStatusCode.Created, transferAC.StatusCode);
        Assert.Equal((await transferAB.Content.ReadFromJsonAsync<IdResponse>())!.Id,
            (await transferReplay.Content.ReadFromJsonAsync<IdResponse>())!.Id);
        Assert.Equal(HttpStatusCode.Conflict, transferConflict.StatusCode);

        using var keyA = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = a });
        using var keyB = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = b });
        keyA.EnsureSuccessStatusCode();
        keyB.EnsureSuccessStatusCode();
        var pix = new { accountId = a, keyId = (await keyA.Content.ReadFromJsonAsync<IdResponse>())!.Id,
            institutionIspb = "00000000", direction = "Incoming", amountMinor = 1, scenario = "Confirm" };
        using var pixA = await PostAsync("/api/v1/pix/operations", pix);
        using var pixB = await PostAsync("/api/v1/pix/operations", new
        {
            accountId = b, keyId = (await keyB.Content.ReadFromJsonAsync<IdResponse>())!.Id,
            institutionIspb = "00000000", direction = "Incoming", amountMinor = 2, scenario = "Confirm"
        });
        using var pixReplay = await PostAsync("/api/v1/pix/operations", pix);
        Assert.Equal(HttpStatusCode.Accepted, pixA.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, pixB.StatusCode);
        Assert.Equal((await pixA.Content.ReadFromJsonAsync<IdResponse>())!.Id,
            (await pixReplay.Content.ReadFromJsonAsync<IdResponse>())!.Id);
        await using var db = CreateContext();
        await Assert.ThrowsAsync<PostgresException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261126140000_AddAccountAccess"));
        Assert.Equal(2, await db.Deposits.CountAsync());
        Assert.Equal(2, await db.Transfers.CountAsync());
        Assert.Equal(2, await db.PixOperations.CountAsync());
        await LoadTestHelpers.AssertLedgerAsync(db);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record AccessAudit(string Action, Guid AccountId, string TargetSubject, string ActorSubject);
}
