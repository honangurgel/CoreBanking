using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoreBanking.Infrastructure;
using DotNet.Testcontainers.Builders;
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

    [Fact]
    public async Task TracesConnectApiWorkerRabbitAndSimulator()
    {
        await using var jaeger = new ContainerBuilder("jaegertracing/jaeger:2.23.0")
            .WithPortBinding(4317, true)
            .WithPortBinding(16686, true)
            .WithCreateParameterModifier(parameters =>
            {
                var bindings = parameters.HostConfig?.PortBindings
                    ?? throw new InvalidOperationException("Portas do Jaeger não foram configuradas.");
                foreach (var binding in bindings.Values.SelectMany(value => value))
                    binding.HostIP = "127.0.0.1";
            })
            .Build();
        await jaeger.StartAsync();
        var environment = new Dictionary<string, string>
        {
            ["ConnectionStrings__Banking"] = postgres.GetConnectionString(),
            ["RabbitMQ__ConnectionString"] = rabbit.GetConnectionString(),
            ["Logging__LogLevel__Default"] = "Warning",
            ["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["Authentication__Mode"] = "Disabled",
            ["Tracing__Enabled"] = "true",
            ["Tracing__OtlpEndpoint"] = $"http://127.0.0.1:{jaeger.GetMappedPublicPort(4317)}"
        };
        await using var simulator = new ApplicationProcess("CoreBanking.Simulator", environment, "http://127.0.0.1:0");
        environment["Pix__SimulatorUrl"] = (await simulator.WaitForAddressAsync()).ToString();
        await using var api = new ApplicationProcess("CoreBanking.Api", environment, "http://127.0.0.1:0");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = await api.WaitForAddressAsync() };
        await using var worker = new ApplicationProcess("CoreBanking.Worker", environment);
        using var query = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{jaeger.GetMappedPublicPort(16686)}")
        };
        var source = await LoadTestHelpers.CreateAccountAsync(client);
        var destination = await LoadTestHelpers.CreateAccountAsync(client);

        async Task<string> PostAsync(string path, string key, object body)
        {
            var traceId = ActivityTraceId.CreateRandom().ToString();
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key);
            request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return traceId;
        }

        await PostAsync("/api/v1/deposits", "trace-deposit", new { accountId = source, amountMinor = 100 });
        var transferTrace = await PostAsync("/api/v1/transfers", "trace-transfer",
            new { sourceAccountId = source, destinationAccountId = destination, amountMinor = 1 });
        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = source });
        keyResponse.EnsureSuccessStatusCode();
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        var pixTrace = await PostAsync("/api/v1/pix/operations", "trace-pix",
            new { accountId = source, keyId = key, institutionIspb = "00000000", direction = "Incoming", amountMinor = 1, scenario = "Confirm" });

        static JsonElement[] Spans(JsonDocument trace) => trace.RootElement.GetProperty("data")[0]
            .GetProperty("spans").EnumerateArray().ToArray();
        static string Id(JsonElement span) => span.GetProperty("spanID").GetString()!;
        static string? Parent(JsonElement span) => span.GetProperty("references").EnumerateArray()
            .Where(item => item.GetProperty("refType").GetString() == "CHILD_OF")
            .Select(item => item.GetProperty("spanID").GetString()).FirstOrDefault();

        async Task<JsonDocument> FindTraceAsync(string traceId, Func<JsonElement[], bool> complete)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                api.ThrowIfExited();
                worker.ThrowIfExited();
                simulator.ThrowIfExited();
                try
                {
                    using var response = await query.GetAsync($"/api/traces/{traceId}");
                    if (response.IsSuccessStatusCode)
                    {
                        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                        var data = document.RootElement.GetProperty("data");
                        if (data.GetArrayLength() > 0)
                        {
                            if (complete(Spans(document))) return document;
                        }
                        document.Dispose();
                    }
                }
                catch (HttpRequestException) when (clock.Elapsed < TimeSpan.FromSeconds(60)) { }
                await Task.Delay(500);
            }
            throw new TimeoutException($"O Jaeger não recebeu todos os spans de {traceId}.");
        }

        using var transfer = await FindTraceAsync(transferTrace, spans =>
        {
            var publisher = spans.FirstOrDefault(span => span.GetProperty("operationName").GetString() == "transfer publish");
            return publisher.ValueKind != JsonValueKind.Undefined
                && spans.Any(span => Id(span) == Parent(publisher))
                && spans.Any(span => span.GetProperty("operationName").GetString() == "transfer process");
        });
        using var pix = await FindTraceAsync(pixTrace, spans =>
        {
            var processing = spans.FirstOrDefault(span => span.GetProperty("operationName").GetString() == "pix process");
            return processing.ValueKind != JsonValueKind.Undefined
                && spans.Any(span => Id(span) == Parent(processing))
                && spans.Any(span => Parent(span) == Id(processing)
                    && spans.Any(child => Parent(child) == Id(span)));
        });

        var transferSpans = Spans(transfer);
        var publisher = Assert.Single(transferSpans, span => span.GetProperty("operationName").GetString() == "transfer publish");
        var consumer = Assert.Single(transferSpans, span => span.GetProperty("operationName").GetString() == "transfer process");
        Assert.Contains(transferSpans, span => Id(span) == Parent(publisher));
        Assert.Equal(Id(publisher), Parent(consumer));

        var pixSpans = Spans(pix);
        var processing = Assert.Single(pixSpans, span => span.GetProperty("operationName").GetString() == "pix process");
        Assert.Contains(pixSpans, span => Id(span) == Parent(processing));
        Assert.Contains(pixSpans, span => Parent(span) == Id(processing)
            && pixSpans.Any(child => Parent(child) == Id(span)));
        var processes = pix.RootElement.GetProperty("data")[0].GetProperty("processes");
        Assert.Contains(pixSpans, span => processes.GetProperty(span.GetProperty("processID").GetString()!)
            .GetProperty("serviceName").GetString() == "corebanking-simulator");
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
