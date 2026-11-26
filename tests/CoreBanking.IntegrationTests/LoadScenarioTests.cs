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

[Collection("Load scenarios")]
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
            builder.UseTestLoopback();
            builder.UseSetting("RateLimiting:PermitsPerMinute", (count + 10).ToString());
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
        using var client = api.CreateClient();
        var source = await LoadTestHelpers.CreateAccountAsync(client);
        var destination = await LoadTestHelpers.CreateAccountAsync(client);
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
        await LoadTestHelpers.AssertLedgerAsync(db);

        var endLoh = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        output.WriteLine($"Transferências: {count}; concorrência máxima: 8; tempo: {clock.Elapsed.TotalSeconds:F2}s; média: {count / clock.Elapsed.TotalMinutes:F0}/min.");
        output.WriteLine($"LOH após último GC observado: início {startLoh} bytes; fim {endLoh} bytes (processo de teste inteiro).");
    }

    private BankingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private sealed record IdResponse(Guid Id);
    private sealed record AccountResponse(long BalanceMinor);
}
