using System.Net;
using System.Net.Http.Json;
using CoreBanking.Domain.Ledger;
using CoreBanking.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CoreBanking.IntegrationTests;

public sealed class TransferApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await postgres.DisposeAsync();

    private BankingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString()));

    private static async Task<Guid> OpenAccountAsync(HttpClient client)
    {
        using var customerResponse = await client.PostAsJsonAsync("/api/v1/customers", new { name = "Cliente fictício" });
        customerResponse.EnsureSuccessStatusCode();
        var customer = await customerResponse.Content.ReadFromJsonAsync<Customer>();
        using var accountResponse = await client.PostAsJsonAsync("/api/v1/accounts", new { customerId = customer!.Id });
        accountResponse.EnsureSuccessStatusCode();
        var account = await accountResponse.Content.ReadFromJsonAsync<Account>();
        return account!.Id;
    }

    private async Task CreditAsync(Guid accountId, long amount)
    {
        await using var db = CreateContext();
        var ledger = await db.LedgerAccounts.AsNoTracking().SingleAsync(item => item.CustomerAccountId == accountId);
        var system = LedgerAccount.ForSystem();
        var writer = new LedgerWriter(db);
        await writer.CreateAccountAsync(system);
        await writer.PostAsync(LedgerEntry.Create("Crédito de teste", [new(ledger.Id, amount), new(system.Id, -amount)]));
    }

    private static async Task<HttpResponseMessage> TransferAsync(HttpClient client, string key, Guid source, Guid destination, long amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers");
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(new { sourceAccountId = source, destinationAccountId = destination, amountMinor = amount });
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task SimulatedDeposit_IsIdempotentAndCreditsAccount()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var account = await OpenAccountAsync(client);
        async Task<HttpResponseMessage> DepositAsync(long amount)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deposits");
            request.Headers.Add("Idempotency-Key", "deposit-test");
            request.Content = JsonContent.Create(new { accountId = account, amountMinor = amount });
            return await client.SendAsync(request);
        }

        var responses = await Task.WhenAll(DepositAsync(100), DepositAsync(100));
        using var first = responses[0];
        using var second = responses[1];
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var deposit = await first.Content.ReadFromJsonAsync<Deposit>();
        Assert.NotNull(deposit);
        Assert.Equal(deposit, await second.Content.ReadFromJsonAsync<Deposit>());
        using var conflict = await DepositAsync(101);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(100, (await client.GetFromJsonAsync<Account>($"/api/v1/accounts/{account}"))!.BalanceMinor);
        Assert.Equal(deposit, await client.GetFromJsonAsync<Deposit>($"/api/v1/deposits/{deposit.Id}"));
        await using var db = CreateContext();
        Assert.Equal(1, await db.Deposits.CountAsync());
        var count = await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"LedgerEntries\"").SingleAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RepeatedTransfer_ReturnsSameResultWithoutDuplicatingPostings()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var source = await OpenAccountAsync(client);
        var destination = await OpenAccountAsync(client);
        await CreditAsync(source, 1000);

        var responses = await Task.WhenAll(TransferAsync(client, "same-key", source, destination, 400),
            TransferAsync(client, "same-key", source, destination, 400));
        using var first = responses[0];
        using var second = responses[1];
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var firstTransfer = await first.Content.ReadFromJsonAsync<Transfer>();
        var secondTransfer = await second.Content.ReadFromJsonAsync<Transfer>();
        Assert.NotNull(firstTransfer);
        Assert.Equal(firstTransfer, secondTransfer);
        using var conflict = await TransferAsync(client, "same-key", source, destination, 401);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var restartedFactory = CreateFactory();
        using var restartedClient = restartedFactory.CreateClient();
        var retrieved = await restartedClient.GetFromJsonAsync<Transfer>($"/api/v1/transfers/{firstTransfer.Id}");
        Assert.Equal(firstTransfer, retrieved);
        Assert.Equal(600, (await restartedClient.GetFromJsonAsync<Account>($"/api/v1/accounts/{source}"))!.BalanceMinor);
        Assert.Equal(400, (await restartedClient.GetFromJsonAsync<Account>($"/api/v1/accounts/{destination}"))!.BalanceMinor);
        await using var db = CreateContext();
        Assert.Equal(1, await db.Transfers.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        var entryCount = await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"LedgerEntries\"").SingleAsync();
        Assert.Equal(2, entryCount);
    }

    [Fact]
    public async Task ConcurrentTransfers_PreventOverdraftAndPersistFailure()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var source = await OpenAccountAsync(client);
        var destination = await OpenAccountAsync(client);
        await CreditAsync(source, 100);
        var responses = await Task.WhenAll(TransferAsync(client, "key-1", source, destination, 80),
            TransferAsync(client, "key-2", source, destination, 80));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            var failure = await responses.Single(response => response.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<Transfer>();
            Assert.NotNull(failure);
            await CreditAsync(source, 100);
            var failedKey = await GetKeyAsync(failure.Id);
            using var retry = await TransferAsync(client, failedKey, source, destination, 80);
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            Assert.Equal(failure, await retry.Content.ReadFromJsonAsync<Transfer>());
            Assert.Equal(120, (await client.GetFromJsonAsync<Account>($"/api/v1/accounts/{source}"))!.BalanceMinor);
            await using var db = CreateContext();
            Assert.Equal(1, await db.OutboxMessages.CountAsync());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task RejectsInvalidAndUnknownAccounts()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var source = await OpenAccountAsync(client);
        using var absentKey = await client.PostAsJsonAsync("/api/v1/transfers", new { sourceAccountId = source, destinationAccountId = Guid.NewGuid(), amountMinor = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, absentKey.StatusCode);
        using var sameAccount = await TransferAsync(client, "key", source, source, 1);
        Assert.Equal(HttpStatusCode.BadRequest, sameAccount.StatusCode);
        using var invalidAmount = await TransferAsync(client, "key", source, Guid.NewGuid(), 0);
        Assert.Equal(HttpStatusCode.BadRequest, invalidAmount.StatusCode);
        using var unknown = await TransferAsync(client, "key", source, Guid.NewGuid(), 1);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await using var db = CreateContext();
        Assert.Equal(0, await db.Transfers.CountAsync());
        Assert.Equal(0, await db.OutboxMessages.CountAsync());
    }

    private async Task<string> GetKeyAsync(Guid transferId)
    {
        await using var db = CreateContext();
        return (await db.Transfers.SingleAsync(item => item.Id == transferId)).IdempotencyKey;
    }

    private sealed record Customer(Guid Id);
    private sealed record Account(Guid Id, long BalanceMinor);
    private sealed record Transfer(Guid Id, Guid SourceAccountId, Guid DestinationAccountId, long AmountMinor, string Status, DateTimeOffset CreatedAt);
    private sealed record Deposit(Guid Id, Guid AccountId, long AmountMinor, DateTimeOffset CreatedAt);
}
