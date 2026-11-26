using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit.Abstractions;

namespace CoreBanking.IntegrationTests;

[CollectionDefinition("Load scenarios", DisableParallelization = true)]
public sealed class LoadScenarioCollection { }

[Collection("Load scenarios")]
public sealed class PixProcessLoadTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync());
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await rabbit.DisposeAsync();
        await postgres.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PixTraffic_RecoversFromOutage_AcrossProcesses(bool workerOutage)
    {
        var configured = Environment.GetEnvironmentVariable("COREBANKING_PIX_LOAD_SECONDS");
        var duration = configured is null ? 10 : int.Parse(configured);
        if (duration is < 10 or > 60)
            throw new InvalidOperationException("COREBANKING_PIX_LOAD_SECONDS deve estar entre 10 e 60.");

        var environment = new Dictionary<string, string>
        {
            ["ConnectionStrings__Banking"] = postgres.GetConnectionString(),
            ["RabbitMQ__ConnectionString"] = rabbit.GetConnectionString(),
            ["Logging__LogLevel__Default"] = "Warning",
            ["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["Authentication__Mode"] = "Disabled"
        };
        await using var simulator = new ApplicationProcess("CoreBanking.Simulator", environment, "http://127.0.0.1:0");
        var simulatorUri = await simulator.WaitForAddressAsync();
        environment["Pix__SimulatorUrl"] = simulatorUri.ToString();
        environment["RateLimiting__PermitsPerMinute"] = "3000";
        await using var api = new ApplicationProcess("CoreBanking.Api", environment, "http://127.0.0.1:0");
        var apiUri = await api.WaitForAddressAsync();
        await using var worker = new ApplicationProcess("CoreBanking.Worker", environment);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = apiUri };
        var account = await LoadTestHelpers.CreateAccountAsync(client);
        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = account });
        keyResponse.EnsureSuccessStatusCode();
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        var counters = Environment.GetEnvironmentVariable("COREBANKING_DOTNET_COUNTERS");
        if (counters is not null && !File.Exists(counters))
            throw new InvalidOperationException("COREBANKING_DOTNET_COUNTERS deve apontar para dotnet-counters.");
        await using var apiSampler = counters is null ? null : new LohSampler(counters, api.Id, duration + 30);
        await using var workerSampler = counters is null || workerOutage ? null : new LohSampler(counters, worker.Id, duration + 30);
        if (apiSampler is not null) await apiSampler.WaitUntilReadyAsync();
        if (workerSampler is not null) await workerSampler.WaitUntilReadyAsync();

        var counts = new long[LatencyBounds.Length];
        using var stop = new CancellationTokenSource();
        var issued = 0;
        var completed = 0;
        var clock = new Stopwatch();
        Task load = Task.CompletedTask;
        ApplicationProcess? restarted = null;
        ApplicationProcess? restartedWorker = null;
        try
        {
            async Task ProduceAsync()
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var index = Interlocked.Increment(ref issued);
                        if (index > 1000) throw new InvalidOperationException("Limite de operações do teste atingido.");
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pix/operations");
                        request.Headers.Add("Idempotency-Key", $"pix-load-{index}");
                        request.Content = JsonContent.Create(new
                        {
                            accountId = account, keyId = key, institutionIspb = "00000000",
                            direction = "Incoming", amountMinor = 1, scenario = "Confirm"
                        });
                        var start = Stopwatch.GetTimestamp();
                        using var response = await client.SendAsync(request);
                        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        var bucket = Array.FindIndex(LatencyBounds, bound => elapsed <= bound);
                        Interlocked.Increment(ref counts[bucket]);
                        Interlocked.Increment(ref completed);
                        await Task.Delay(400);
                    }
                }
                finally { stop.Cancel(); }
            }

            clock.Start();
            load = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ProduceAsync()));
            await WaitForAsync(() => Task.FromResult(Volatile.Read(ref completed) >= 5), load, worker, api);
            if (workerOutage) await worker.StopAsync();
            else await simulator.StopAsync();
            var beforeOutage = Volatile.Read(ref completed);
            var outageKey = $"pix-load-{Volatile.Read(ref issued) + 1}";
            await WaitForAsync(async () =>
            {
                await using var db = CreateContext();
                return Volatile.Read(ref completed) >= beforeOutage + 5
                    && await db.PixOperations.AnyAsync(item => item.IdempotencyKey == outageKey
                        && (workerOutage ? item.Status == "Pending" : item.Attempts > 0));
            }, load, workerOutage ? null : worker, api);
            var duringOutage = Volatile.Read(ref completed) - beforeOutage;

            if (workerOutage) restartedWorker = new ApplicationProcess("CoreBanking.Worker", environment);
            else
            {
                restarted = new ApplicationProcess("CoreBanking.Simulator", environment, simulatorUri.ToString());
                Assert.Equal(simulatorUri, await restarted.WaitForAddressAsync());
            }
            var activeWorker = restartedWorker ?? worker;
            var beforeRecovery = Volatile.Read(ref completed);
            await WaitForAsync(() => Task.FromResult(Volatile.Read(ref completed) >= beforeRecovery + 5
                && clock.Elapsed >= TimeSpan.FromSeconds(duration)), load, activeWorker, api, TimeSpan.FromSeconds(duration + 60));
            stop.Cancel();
            await load;
            clock.Stop();
            var total = Volatile.Read(ref completed);
            if (apiSampler is not null && workerSampler is not null)
            {
                var samples = await Task.WhenAll(apiSampler.FinishAsync(), workerSampler.FinishAsync());
                output.WriteLine($"Pico de LOH amostrado após GC: API {samples[0].PeakBytes:F0} bytes ({samples[0].Samples} amostras); worker {samples[1].PeakBytes:F0} bytes ({samples[1].Samples} amostras).");
            }
            else if (apiSampler is not null)
            {
                var sample = await apiSampler.FinishAsync();
                output.WriteLine($"Pico de LOH amostrado após GC: API {sample.PeakBytes:F0} bytes ({sample.Samples} amostras); worker não medido durante reinício.");
            }

            await WaitForAsync(async () =>
            {
                await using var db = CreateContext();
                var reviewIds = await db.PixOperations.AsNoTracking().Where(item => item.Status == "RequiresReview")
                    .OrderBy(item => item.Id).Select(item => item.Id).Take(20).ToListAsync();
                foreach (var id in reviewIds)
                {
                    using var response = await client.PostAsync($"/api/v1/pix/operations/{id}/retry", null);
                    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                }
                return await db.PixOperations.CountAsync(item => item.Status == "Confirmed") == total;
            }, load, activeWorker, api, TimeSpan.FromSeconds(180));

            await using var verification = CreateContext();
            Assert.Equal(total, await verification.PixOperations.CountAsync());
            Assert.Equal(total, await verification.PixOperations.CountAsync(item => item.Status == "Confirmed"));
            Assert.Equal(total, await verification.PixOperations.CountAsync(item => item.SettlementEntryId != null));
            Assert.Equal(total, (await client.GetFromJsonAsync<AccountBalance>($"/api/v1/accounts/{account}"))!.BalanceMinor);
            await LoadTestHelpers.AssertLedgerAsync(verification);
            output.WriteLine($"Indisponibilidade: {(workerOutage ? "worker" : "simulador")}; Pix: {total}; tráfego: {clock.Elapsed.TotalSeconds:F2}s; durante indisponibilidade: {duringOutage}; concorrência: 4.");
            output.WriteLine($"Latência de aceitação HTTP, limites superiores aproximados: p50={Percentile(counts, 0.50)}ms, p95={Percentile(counts, 0.95)}ms, p99={Percentile(counts, 0.99)}ms.");
        }
        finally
        {
            stop.Cancel();
            try { await load; }
            finally
            {
                if (restarted is not null) await restarted.DisposeAsync();
                if (restartedWorker is not null) await restartedWorker.DisposeAsync();
            }
        }
    }

    private static readonly double[] LatencyBounds = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, double.PositiveInfinity];

    private static double Percentile(long[] counts, double percentile)
    {
        var target = (long)Math.Ceiling(counts.Sum() * percentile);
        long accumulated = 0;
        for (var index = 0; index < counts.Length; index++)
        {
            accumulated += counts[index];
            if (accumulated >= target) return LatencyBounds[index];
        }
        throw new InvalidOperationException("Nenhuma latência foi registrada.");
    }

    private BankingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private static async Task WaitForAsync(Func<Task<bool>> condition, Task load, ApplicationProcess? worker,
        ApplicationProcess api, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < (timeout ?? TimeSpan.FromSeconds(60)))
        {
            worker?.ThrowIfExited();
            api.ThrowIfExited();
            if (load.IsFaulted) await load;
            if (await condition()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("O cenário Pix não atingiu a condição esperada.");
    }

    private sealed record IdResponse(Guid Id);
    private sealed record AccountBalance(long BalanceMinor);
}
