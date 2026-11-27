using System.Net;
using System.Net.Http.Json;
using CoreBanking.Application;
using CoreBanking.Application.Cards;
using CoreBanking.Infrastructure;
using CoreBanking.Infrastructure.Cards;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoreBanking.IntegrationTests;

public sealed class DebitCardTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task AuthorizationAndSettlement_AreIdempotentAndAudited()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        var account = await LoadTestHelpers.CreateAccountAsync(client);
        await DepositAsync(client, account, 1000);
        var issues = await Task.WhenAll(
            PostAsync(client, "/api/v1/debit-cards", "issue", new { accountId = account }),
            PostAsync(client, "/api/v1/debit-cards", "issue", new { accountId = account }));
        using var firstIssue = issues[0];
        using var secondIssue = issues[1];
        Assert.All(issues, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var card = (await firstIssue.Content.ReadFromJsonAsync<DebitCardResult>())!;
        Assert.Equal(card.Id, (await secondIssue.Content.ReadFromJsonAsync<DebitCardResult>())!.Id);

        var path = $"/api/v1/debit-cards/{card.Id}/authorizations";
        var requests = await Task.WhenAll(
            PostAsync(client, path, "purchase", new { amountMinor = 400 }),
            PostAsync(client, path, "purchase", new { amountMinor = 400 }));
        using var first = requests[0];
        using var second = requests[1];
        Assert.All(requests, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var authorization = (await first.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!;
        Assert.Equal(authorization.Id, (await second.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!.Id);
        Assert.Equal("Authorized", authorization.Status);
        Assert.Equal(600, await BalanceAsync(client, account));
        using var conflict = await PostAsync(client, path, "purchase", new { amountMinor = 401 });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var anotherCardResponse = await PostAsync(client, "/api/v1/debit-cards", "another-card", new { accountId = account });
        var anotherCard = (await anotherCardResponse.Content.ReadFromJsonAsync<DebitCardResult>())!;
        using var differentCard = await PostAsync(client, $"/api/v1/debit-cards/{anotherCard.Id}/authorizations",
            "purchase", new { amountMinor = 400 });
        Assert.Equal(HttpStatusCode.Conflict, differentCard.StatusCode);
        using var invalidAmount = await PostAsync(client, path, "invalid", new { amountMinor = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAmount.StatusCode);

        var finalPath = $"/api/v1/debit-authorizations/{authorization.Id}";
        using var partial = await client.PostAsJsonAsync($"{finalPath}/settle", new { amountMinor = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
        var settlements = await Task.WhenAll(client.PostAsync($"{finalPath}/settle", null),
            client.PostAsync($"{finalPath}/settle", null));
        using var firstSettlement = settlements[0];
        using var secondSettlement = settlements[1];
        Assert.All(settlements, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var settled = (await firstSettlement.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!;
        Assert.Equal("Settled", settled.Status);
        Assert.NotNull(settled.ReservationEntryId);
        Assert.NotNull(settled.FinalizationEntryId);
        Assert.Equal(settled.FinalizationEntryId, (await secondSettlement.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!.FinalizationEntryId);
        using var cancelled = await client.PostAsync($"{finalPath}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        Assert.Equal(600, await BalanceAsync(client, account));

        await using var db = CreateContext();
        Assert.Equal(2, await db.DebitCards.CountAsync());
        Assert.Equal(1, await db.DebitAuthorizations.CountAsync());
        Assert.Equal(0, await InternalBalanceAsync(db, DebitCardService.ReserveAccountId));
        Assert.Equal(400, await InternalBalanceAsync(db, DebitCardService.ClearingAccountId));
        Assert.Equal(1, await AuditCountAsync(db, card.Id, "CardIssue"));
        Assert.Equal(1, await AuditCountAsync(db, authorization.Id, "CardAuthorize"));
        Assert.Equal(1, await AuditCountAsync(db, authorization.Id, "CardSettle"));
        await LoadTestHelpers.AssertLedgerAsync(db);
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>()
            .MigrateAsync("20261127011543_PersistTraceContext"));
        Assert.Equal(1, await db.DebitAuthorizations.CountAsync());
    }

    [Fact]
    public async Task CancellationRestoresFunds_AndInsufficientFundsReplayDoesNotReserve()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        var account = await LoadTestHelpers.CreateAccountAsync(client);
        await DepositAsync(client, account, 100);
        using var issued = await PostAsync(client, "/api/v1/debit-cards", "issue", new { accountId = account });
        var card = (await issued.Content.ReadFromJsonAsync<DebitCardResult>())!;
        var path = $"/api/v1/debit-cards/{card.Id}/authorizations";
        var concurrent = await Task.WhenAll(PostAsync(client, path, "a", new { amountMinor = 70 }),
            PostAsync(client, path, "b", new { amountMinor = 70 }));
        using var first = concurrent[0];
        using var second = concurrent[1];
        var accepted = Assert.Single(concurrent, response => response.StatusCode == HttpStatusCode.Created);
        var rejected = Assert.Single(concurrent, response => response.StatusCode == HttpStatusCode.Conflict);
        var authorization = (await accepted.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!;
        var insufficient = (await rejected.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!;
        Assert.Equal("InsufficientFunds", insufficient.Status);
        Assert.Null(insufficient.ReservationEntryId);
        Assert.Equal(30, await BalanceAsync(client, account));

        var finalPath = $"/api/v1/debit-authorizations/{authorization.Id}";
        using var cancellation = await client.PostAsync($"{finalPath}/cancel", null);
        using var replayCancellation = await client.PostAsync($"{finalPath}/cancel", null);
        using var wrongFinalization = await client.PostAsync($"{finalPath}/settle", null);
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayCancellation.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, wrongFinalization.StatusCode);
        Assert.Equal(100, await BalanceAsync(client, account));
        var failedKey = accepted == first ? "b" : "a";
        using var replayInsufficient = await PostAsync(client, path, failedKey, new { amountMinor = 70 });
        Assert.Equal(HttpStatusCode.Conflict, replayInsufficient.StatusCode);
        Assert.Equal(insufficient.Id, (await replayInsufficient.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!.Id);
        using var cancelInsufficient = await client.PostAsync($"/api/v1/debit-authorizations/{insufficient.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, cancelInsufficient.StatusCode);
        Assert.Equal(100, await BalanceAsync(client, account));

        await using var db = CreateContext();
        Assert.Equal(0, await InternalBalanceAsync(db, DebitCardService.ReserveAccountId));
        Assert.Equal(1, await AuditCountAsync(db, authorization.Id, "CardCancel"));
        Assert.Equal(1, await AuditCountAsync(db, insufficient.Id, "CardAuthorize"));
        await LoadTestHelpers.AssertLedgerAsync(db);
    }

    [Fact]
    public async Task SettlementAndCancellation_RaceWithOnlyOneFinalization()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        var account = await LoadTestHelpers.CreateAccountAsync(client);
        await DepositAsync(client, account, 100);
        using var issued = await PostAsync(client, "/api/v1/debit-cards", "issue", new { accountId = account });
        var card = (await issued.Content.ReadFromJsonAsync<DebitCardResult>())!;
        using var created = await PostAsync(client, $"/api/v1/debit-cards/{card.Id}/authorizations", "purchase", new { amountMinor = 70 });
        var authorization = (await created.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!;
        var path = $"/api/v1/debit-authorizations/{authorization.Id}";
        var results = await Task.WhenAll(client.PostAsync($"{path}/settle", null), client.PostAsync($"{path}/cancel", null));
        using var first = results[0];
        using var second = results[1];
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        var final = (await client.GetFromJsonAsync<DebitAuthorizationResult>(path))!;
        Assert.Contains(final.Status, new[] { "Settled", "Cancelled" });
        Assert.Equal(final.Status == "Settled" ? 30 : 100, await BalanceAsync(client, account));
        await using var db = CreateContext();
        Assert.Equal(0, await InternalBalanceAsync(db, DebitCardService.ReserveAccountId));
        Assert.Equal(1, await AuditCountAsync(db, authorization.Id, final.Status == "Settled" ? "CardSettle" : "CardCancel"));
        Assert.Equal(0, await AuditCountAsync(db, authorization.Id, final.Status == "Settled" ? "CardCancel" : "CardSettle"));
        await LoadTestHelpers.AssertLedgerAsync(db);
    }

    [Fact]
    public async Task AuditFailure_RollsBackCancellationAndLedger()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        var account = await LoadTestHelpers.CreateAccountAsync(client);
        await DepositAsync(client, account, 100);
        using var issued = await PostAsync(client, "/api/v1/debit-cards", "issue", new { accountId = account });
        var card = (await issued.Content.ReadFromJsonAsync<DebitCardResult>())!;
        using var created = await PostAsync(client, $"/api/v1/debit-cards/{card.Id}/authorizations", "purchase", new { amountMinor = 70 });
        var authorization = (await created.Content.ReadFromJsonAsync<DebitAuthorizationResult>())!;
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "OperationAuditEvents" ADD CONSTRAINT "test_no_card_cancel" CHECK ("Action" <> 'CardCancel')""");
        var service = new DebitCardService(db);
        await Assert.ThrowsAsync<PostgresException>(() =>
            service.FinalizeAsync(authorization.Id, false, AuditActor.Local("test"), CancellationToken.None));
        await using var verify = CreateContext();
        var stored = await verify.DebitAuthorizations.AsNoTracking().SingleAsync(item => item.Id == authorization.Id);
        Assert.Equal("Authorized", stored.Status);
        Assert.Null(stored.FinalizationEntryId);
        Assert.Equal(70, await InternalBalanceAsync(verify, DebitCardService.ReserveAccountId));
        Assert.Equal(30, await BalanceAsync(client, account));
        await LoadTestHelpers.AssertLedgerAsync(verify);
    }

    private BankingDbContext CreateContext() => new(new DbContextOptionsBuilder<BankingDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);
    private WebApplicationFactory<Program> CreateApi() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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

    private static async Task DepositAsync(HttpClient client, Guid accountId, long amountMinor)
    {
        using var response = await PostAsync(client, "/api/v1/deposits", "deposit", new { accountId, amountMinor });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<long> BalanceAsync(HttpClient client, Guid accountId) =>
        (await client.GetFromJsonAsync<AccountBalance>($"/api/v1/accounts/{accountId}"))!.BalanceMinor;

    private static Task<long> InternalBalanceAsync(BankingDbContext db, Guid id) =>
        db.LedgerAccounts.Where(item => item.Id == id).Select(item => item.BalanceMinor).SingleAsync();

    private static Task<long> AuditCountAsync(BankingDbContext db, Guid id, string action) =>
        db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM "OperationAuditEvents" WHERE "OperationId" = {id} AND "Action" = {action}
            """).SingleAsync();

    private sealed record AccountBalance(long BalanceMinor);
}
