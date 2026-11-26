using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using CoreBanking.Infrastructure;
using CoreBanking.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit.Abstractions;

namespace CoreBanking.IntegrationTests;

[Collection("Load scenarios")]
public sealed class RecoveryLoadTests(ITestOutputHelper output) : IAsyncLifetime
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
    public async Task SustainedTraffic_BrokerOutage_RecoversWithoutLostTransfers()
    {
        const int maxOperations = 5000;
        var configured = Environment.GetEnvironmentVariable("COREBANKING_RECOVERY_SECONDS");
        var duration = configured is null ? 10 : int.Parse(configured);
        if (duration is < 10 or > 120)
            throw new InvalidOperationException("COREBANKING_RECOVERY_SECONDS deve estar entre 10 e 120.");

        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
            builder.UseSetting("RateLimiting:PermitsPerMinute", "6000");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
        api.UseKestrel(0);
        using var client = api.CreateClient();
        Assert.True(client.BaseAddress!.IsLoopback);
        var source = await LoadTestHelpers.CreateAccountAsync(client);
        var destination = await LoadTestHelpers.CreateAccountAsync(client);
        using (var deposit = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deposits"))
        {
            deposit.Headers.Add("Idempotency-Key", "recovery-deposit");
            deposit.Content = JsonContent.Create(new { accountId = source, amountMinor = maxOperations });
            using var response = await client.SendAsync(deposit);
            response.EnsureSuccessStatusCode();
        }

        await using var services = new ServiceCollection()
            .AddDbContext<BankingDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()))
            .AddScoped<ITransferEventProcessor, TransferEventProcessor>()
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:ConnectionString"] = rabbit.GetConnectionString()
        }).Build();
        var logger = new RecoveryLogger();
        using var worker = new CoreBanking.Worker.Worker(services.GetRequiredService<IServiceScopeFactory>(), configuration, logger);
        using var stopLoad = new CancellationTokenSource();
        await worker.StartAsync(CancellationToken.None);
        Task load = Task.CompletedTask;
        var started = new Stopwatch();
        var issued = 0;
        var completed = 0;
        try
        {
            await WaitForAsync(() => Task.FromResult(logger.Starts >= 1), worker, load);
            async Task ProduceAsync()
            {
                try
                {
                    while (!stopLoad.IsCancellationRequested)
                    {
                        var index = Interlocked.Increment(ref issued);
                        if (index > maxOperations) throw new InvalidOperationException("Limite de operações do teste atingido.");
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers");
                        request.Headers.Add("Idempotency-Key", $"recovery-{index}");
                        request.Content = JsonContent.Create(new
                        {
                            sourceAccountId = source, destinationAccountId = destination, amountMinor = 1
                        });
                        using var response = await client.SendAsync(request);
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        Interlocked.Increment(ref completed);
                        await Task.Delay(200);
                    }
                }
                finally { stopLoad.Cancel(); }
            }

            var startLoh = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
            started.Start();
            load = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ProduceAsync()));
            await WaitForAsync(() => Task.FromResult(Volatile.Read(ref completed) >= 5), worker, load);

            var stopped = await rabbit.ExecAsync(["rabbitmqctl", "stop_app"]);
            Assert.True(stopped.ExitCode == 0, $"Falha ao parar RabbitMQ: {stopped.Stderr}");
            var beforeOutageTraffic = Volatile.Read(ref completed);
            await WaitForAsync(() => Task.FromResult(logger.Failures > 0
                && Volatile.Read(ref completed) >= beforeOutageTraffic + 5), worker, load);
            var duringOutage = Volatile.Read(ref completed) - beforeOutageTraffic;
            await using (var db = CreateContext())
                Assert.True(await db.OutboxMessages.AnyAsync(item => item.PublishedAt == null));

            var startedBroker = await rabbit.ExecAsync(["rabbitmqctl", "start_app"]);
            Assert.True(startedBroker.ExitCode == 0, $"Falha ao iniciar RabbitMQ: {startedBroker.Stderr}");
            var beforeRecoveryTraffic = Volatile.Read(ref completed);
            await WaitForAsync(() => Task.FromResult(logger.Starts >= 2
                && Volatile.Read(ref completed) >= beforeRecoveryTraffic + 5
                && started.Elapsed >= TimeSpan.FromSeconds(duration)), worker, load,
                TimeSpan.FromSeconds(duration + 60));

            stopLoad.Cancel();
            await load;
            started.Stop();
            var total = Volatile.Read(ref completed);
            var endLoh = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
            var drain = Stopwatch.StartNew();
            await WaitForAsync(async () =>
            {
                await using var db = CreateContext();
                return await db.OutboxMessages.CountAsync(item => item.PublishedAt != null) == total
                    && await db.InboxMessages.CountAsync() == total;
            }, worker, load, TimeSpan.FromSeconds(120));

            await using var verification = CreateContext();
            Assert.Equal(total, await verification.Transfers.CountAsync());
            Assert.Equal(total, await verification.OutboxMessages.CountAsync());
            Assert.Equal(total, await verification.InboxMessages.CountAsync());
            Assert.Equal(maxOperations - total, (await client.GetFromJsonAsync<AccountResponse>($"/api/v1/accounts/{source}"))!.BalanceMinor);
            Assert.Equal(total, (await client.GetFromJsonAsync<AccountResponse>($"/api/v1/accounts/{destination}"))!.BalanceMinor);
            await LoadTestHelpers.AssertLedgerAsync(verification);
            drain.Stop();
            output.WriteLine($"Transferências: {total}; tráfego ativo: {started.Elapsed.TotalSeconds:F2}s; durante indisponibilidade observada: {duringOutage}; drenagem e validação: {drain.Elapsed.TotalSeconds:F2}s; concorrência: 4.");
            output.WriteLine($"LOH após último GC observado: início {startLoh} bytes; fim {endLoh} bytes (processo de teste inteiro).");
        }
        finally
        {
            stopLoad.Cancel();
            try { await load; }
            finally { await worker.StopAsync(CancellationToken.None); }
        }
    }

    private BankingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private static async Task WaitForAsync(Func<Task<bool>> condition, CoreBanking.Worker.Worker worker, Task load, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < (timeout ?? TimeSpan.FromSeconds(60)))
        {
            if (worker.ExecuteTask is { IsFaulted: true }) await worker.ExecuteTask;
            if (load.IsFaulted) await load;
            if (await condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("O cenário de carga não atingiu a condição esperada.");
    }

    private sealed class RecoveryLogger : ILogger<CoreBanking.Worker.Worker>
    {
        private int starts;
        private int failures;
        public int Starts => Volatile.Read(ref starts);
        public int Failures => Volatile.Read(ref failures);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> properties || properties.Count == 0) return;
            var template = properties[^1].Value as string;
            if (template == "Worker de eventos iniciado.") Interlocked.Increment(ref starts);
            if (template == "Falha temporária no worker; nova tentativa em {Delay}.") Interlocked.Increment(ref failures);
        }
    }

    private sealed record AccountResponse(long BalanceMinor);
}
