using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using CoreBanking.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace CoreBanking.IntegrationTests;

public sealed class LoadScenarioTests(ITestOutputHelper output) : IAsyncLifetime
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
    public async Task ConcurrentTransfers_PreserveLedgerAndIdempotency()
    {
        var configured = Environment.GetEnvironmentVariable("COREBANKING_LOAD_OPERATIONS");
        var count = configured is null ? 40 : int.Parse(configured);
        if (count is < 1 or > 10000)
            throw new InvalidOperationException("COREBANKING_LOAD_OPERATIONS deve estar entre 1 e 10000.");

        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
            builder.UseSetting("RateLimiting:PermitsPerMinute", (count + 10).ToString());
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
        using var client = api.CreateClient();
        var source = await CreateAccountAsync(client);
        var destination = await CreateAccountAsync(client);
        using (var deposit = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deposits"))
        {
            deposit.Headers.Add("Idempotency-Key", "load-deposit");
            deposit.Content = JsonContent.Create(new { accountId = source, amountMinor = count });
            using var response = await client.SendAsync(deposit);
            response.EnsureSuccessStatusCode();
        }

        async Task<Guid> TransferAsync(int index)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers");
            request.Headers.Add("Idempotency-Key", $"load-{index}");
            request.Content = JsonContent.Create(new { sourceAccountId = source, destinationAccountId = destination, amountMinor = 1 });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        }

        var startLoh = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        var clock = Stopwatch.StartNew();
        var firstId = await TransferAsync(0);
        var next = 0;
        var workers = Enumerable.Range(0, Math.Min(8, count)).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= count) break;
                await TransferAsync(index);
            }
        });
        await Task.WhenAll(workers);
        clock.Stop();
        Assert.Equal(firstId, await TransferAsync(0));

        var sourceAccount = await client.GetFromJsonAsync<AccountResponse>($"/api/v1/accounts/{source}");
        var destinationAccount = await client.GetFromJsonAsync<AccountResponse>($"/api/v1/accounts/{destination}");
        Assert.Equal(0, sourceAccount!.BalanceMinor);
        Assert.Equal(count, destinationAccount!.BalanceMinor);

        await using var db = CreateContext();
        Assert.Equal(count, await db.Transfers.CountAsync());
        Assert.Equal(count, await db.OutboxMessages.CountAsync());
        var invalidEntries = await db.Database.SqlQueryRaw<long>(
            """SELECT count(*) AS "Value" FROM "LedgerEntries" WHERE NOT "Posted" """).SingleAsync();
        var negativeBalances = await db.LedgerAccounts.CountAsync(item => item.CustomerAccountId != null && item.BalanceMinor < 0);
        var unbalanced = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM (
                SELECT e."Id"
                FROM "LedgerEntries" e LEFT JOIN "LedgerPostings" p ON p."EntryId" = e."Id"
                WHERE e."Posted" GROUP BY e."Id"
                HAVING count(p."LedgerAccountId") < 2 OR COALESCE(sum(p."AmountMinor"), 0) <> 0
            ) AS invalid
            """).SingleAsync();
        var mismatched = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM "LedgerAccounts" a
            LEFT JOIN (
                SELECT p."LedgerAccountId", sum(p."AmountMinor") AS total
                FROM "LedgerPostings" p JOIN "LedgerEntries" e ON e."Id" = p."EntryId" AND e."Posted"
                GROUP BY p."LedgerAccountId"
            ) p ON p."LedgerAccountId" = a."Id"
            WHERE a."BalanceMinor" <> COALESCE(p.total, 0)
            """).SingleAsync();
        Assert.Equal(0, unbalanced);
        Assert.Equal(0, mismatched);
        Assert.Equal(0, invalidEntries);
        Assert.Equal(0, negativeBalances);

        var endLoh = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        output.WriteLine($"Transferências: {count}; concorrência máxima: 8; tempo: {clock.Elapsed.TotalSeconds:F2}s; média: {count / clock.Elapsed.TotalMinutes:F0}/min.");
        output.WriteLine($"LOH após último GC observado: início {startLoh} bytes; fim {endLoh} bytes (processo de teste inteiro).");
    }

    private BankingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        using var customer = await client.PostAsJsonAsync("/api/v1/customers", new { name = "Carga fictícia" });
        customer.EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        using var account = await client.PostAsJsonAsync("/api/v1/accounts", new { customerId });
        account.EnsureSuccessStatusCode();
        return (await account.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private sealed record IdResponse(Guid Id);
    private sealed record AccountResponse(long BalanceMinor);
}
