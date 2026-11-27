using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoreBanking.Infrastructure;
using CoreBanking.Infrastructure.Messaging;
using CoreBanking.Infrastructure.Transfers;
using CoreBanking.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace CoreBanking.IntegrationTests;

public sealed class TracingConfigurationTests
{
    [Theory]
    [InlineData("http://example.com:4317")]
    [InlineData("http://localhost:4317")]
    [InlineData("https://127.0.0.1:4317")]
    [InlineData("http://127.0.0.1:4317/path")]
    [InlineData("http://127.0.0.1:4317/?token=secret")]
    [InlineData("http://user:password@127.0.0.1:4317")]
    public void EnabledTracing_RejectsNonLocalOrUnsafeEndpoints(string endpoint)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tracing:Enabled"] = "true",
            ["Tracing:OtlpEndpoint"] = endpoint
        }).Build();
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddLocalTracing(config, "test", _ => { }));
    }

    [Fact]
    public void DisabledTracing_DoesNotConfigureExporter()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tracing:OtlpEndpoint"] = "http://example.com"
        }).Build();
        var services = new ServiceCollection();
        services.AddLocalTracing(config, "test", _ => throw new InvalidOperationException());
        Assert.Empty(services);
    }
}

public sealed class MessagingWorkerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync());
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task BrokerOutage_PreservesOutboxAndWorkerRecovers()
    {
        var services = new ServiceCollection()
            .AddDbContext<BankingDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()))
            .AddScoped<CoreBanking.Worker.ITransferEventProcessor, CoreBanking.Worker.TransferEventProcessor>()
            .BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:ConnectionString"] = rabbit.GetConnectionString()
        }).Build();
        var logger = new RecordingLogger();
        var worker = new CoreBanking.Worker.Worker(services.GetRequiredService<IServiceScopeFactory>(), config, logger);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => Task.FromResult(logger.Messages.Any(message => message.Contains("Worker de eventos iniciado."))), worker);
            var stopped = await rabbit.ExecAsync(["rabbitmqctl", "stop_app"]);
            Assert.True(stopped.ExitCode == 0, $"Falha ao parar RabbitMQ: {stopped.Stderr}");
            await WaitForAsync(() => Task.FromResult(logger.Messages.Any(message => message.Contains("Falha temporária no worker"))), worker);

            var transferId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            await using (var db = CreateContext())
            {
                db.Transfers.Add(new TransferRecord
                {
                    Id = transferId,
                    IdempotencyKey = "outage-test",
                    SourceAccountId = Guid.NewGuid(),
                    DestinationAccountId = Guid.NewGuid(),
                    AmountMinor = 1,
                    Status = "Completed",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                db.OutboxMessages.Add(new OutboxMessage { Id = messageId, TransferId = transferId, CreatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            await using (var db = CreateContext())
            {
                Assert.Null((await db.OutboxMessages.SingleAsync(item => item.Id == messageId)).PublishedAt);
                Assert.False(await db.InboxMessages.AnyAsync(item => item.Id == messageId));
            }

            var started = await rabbit.ExecAsync(["rabbitmqctl", "start_app"]);
            Assert.True(started.ExitCode == 0, $"Falha ao iniciar RabbitMQ: {started.Stderr}");
            await WaitForAsync(async () =>
            {
                await using var db = CreateContext();
                return await db.InboxMessages.CountAsync(item => item.Id == messageId) == 1
                    && await db.OutboxMessages.AnyAsync(item => item.Id == messageId && item.PublishedAt != null);
            }, worker);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await services.DisposeAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await rabbit.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private BankingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    [Fact]
    public async Task PublishesAndDeduplicatesMessages_AndDeadLettersInvalidPayload()
    {
        using var metrics = new WorkerMetricRecorder();
        var transferId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await using (var db = CreateContext())
        {
            db.Transfers.Add(new TransferRecord
            {
                Id = transferId,
                IdempotencyKey = "event-test",
                SourceAccountId = Guid.NewGuid(),
                DestinationAccountId = Guid.NewGuid(),
                AmountMinor = 1,
                Status = "Completed",
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.OutboxMessages.Add(new OutboxMessage { Id = messageId, TransferId = transferId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Simulate a confirmed publish followed by a crash before the Outbox was marked.
        var initialFactory = new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) };
        await using (var initialConnection = await initialFactory.CreateConnectionAsync())
        await using (var initialChannel = await initialConnection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)))
        {
            await CoreBanking.Worker.TransferQueues.DeclareAsync(initialChannel, CancellationToken.None);
            var body = JsonSerializer.SerializeToUtf8Bytes(new { MessageId = messageId, TransferId = transferId });
            await initialChannel.BasicPublishAsync("", CoreBanking.Worker.TransferQueues.Main, mandatory: true,
                basicProperties: new BasicProperties { Persistent = true }, body: body);
        }

        var services = new ServiceCollection()
            .AddDbContext<BankingDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()))
            .AddScoped<CoreBanking.Worker.ITransferEventProcessor, CoreBanking.Worker.TransferEventProcessor>()
            .BuildServiceProvider();
        using var logs = new RecordingLogProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:ConnectionString"] = rabbit.GetConnectionString()
        }).Build();
        var worker = new CoreBanking.Worker.Worker(services.GetRequiredService<IServiceScopeFactory>(), config,
            loggerFactory.CreateLogger<CoreBanking.Worker.Worker>());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () =>
            {
                await using var db = CreateContext();
                return await db.InboxMessages.AnyAsync(item => item.Id == messageId)
                    && await db.OutboxMessages.AnyAsync(item => item.Id == messageId && item.PublishedAt != null);
            }, worker);

            var factory = new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            var duplicate = JsonSerializer.SerializeToUtf8Bytes(new { MessageId = messageId, TransferId = transferId });
            await channel.BasicPublishAsync("", "corebanking.transfers.completed", duplicate);
            await channel.BasicPublishAsync("", "corebanking.transfers.completed", mandatory: true,
                basicProperties: new BasicProperties
                {
                    Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()),
                    Headers = new Dictionary<string, object?> { ["traceparent"] = new string('x', 55) }
                }, body: Encoding.UTF8.GetBytes("invalid json"));

            await WaitForAsync(async () =>
            {
                var result = await channel.QueueDeclarePassiveAsync("corebanking.transfers.failed");
                return result.MessageCount == 1 && metrics.Counts.Any(item =>
                    item.Name == "corebanking.transfer.messages" && item.Tag == "invalid");
            }, worker);
            await using var verification = CreateContext();
            Assert.Equal(1, await verification.InboxMessages.CountAsync(item => item.Id == messageId));
            Assert.Contains(logs.Entries, item =>
                Equals(item.Properties.GetValueOrDefault("TransferId"), transferId)
                && Equals(item.Properties.GetValueOrDefault("MessageId"), messageId)
                && item.Properties.ContainsKey("Attempt"));
            Assert.Contains(logs.Entries, item =>
                Equals(item.Properties.GetValueOrDefault("TransferId"), transferId)
                && Equals(item.Properties.GetValueOrDefault("MessageId"), messageId)
                && !item.Properties.ContainsKey("Attempt"));
            Assert.Contains(metrics.Counts, item => item.Name == "corebanking.transfer.messages" && item.Tag == "published");
            Assert.Contains(metrics.Counts, item => item.Name == "corebanking.transfer.messages" && item.Tag == "acknowledged");
            Assert.Contains(metrics.Counts, item => item.Name == "corebanking.transfer.messages" && item.Tag == "invalid");
            Assert.True(metrics.Counts.Count(item => item.Name == "corebanking.rabbitmq.delivery.age.unavailable") >= 3);
            Assert.Contains(metrics.Durations, item => item.Name == "corebanking.rabbitmq.delivery.age" && item.Value >= 0);
            Assert.Contains(logs.Entries, item =>
                item.Properties.GetValueOrDefault("{OriginalFormat}") is string message
                && message.Contains("Cabeçalho de rastreamento inválido"));
            Assert.Contains(metrics.Counts, item => item.Name == "corebanking.rabbitmq.queue.ready"
                && item.Tag == CoreBanking.Worker.TransferQueues.Main && item.Value >= 0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await services.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProcessingFailures_AreRetriedAndCanBeReprocessed(int failures)
    {
        using var metrics = new WorkerMetricRecorder();
        var traceId = ActivityTraceId.CreateRandom();
        var parentId = ActivitySpanId.CreateRandom();
        var traceParent = $"00-{traceId}-{parentId}-01";
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "CoreBanking.Worker",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var transferId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await using (var db = CreateContext())
        {
            db.Transfers.Add(new TransferRecord
            {
                Id = transferId,
                IdempotencyKey = "retry-test",
                SourceAccountId = Guid.NewGuid(),
                DestinationAccountId = Guid.NewGuid(),
                AmountMinor = 1,
                Status = "Completed",
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = messageId, TransferId = transferId, CreatedAt = DateTimeOffset.UtcNow, TraceParent = traceParent
            });
            await db.SaveChangesAsync();
        }

        var processor = new FailingProcessor(failures, postgres.GetConnectionString());
        var services = new ServiceCollection()
            .AddDbContext<BankingDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()))
            .AddSingleton<CoreBanking.Worker.ITransferEventProcessor>(processor)
            .BuildServiceProvider();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:ConnectionString"] = rabbit.GetConnectionString()
        }).Build();
        var worker = new CoreBanking.Worker.Worker(services.GetRequiredService<IServiceScopeFactory>(), config,
            loggerFactory.CreateLogger<CoreBanking.Worker.Worker>());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
            await CoreBanking.Worker.TransferQueues.DeclareAsync(channel, CancellationToken.None);

            if (failures == 3)
            {
                await WaitForAsync(async () =>
                    (await channel.QueueDeclarePassiveAsync(CoreBanking.Worker.TransferQueues.Failed)).MessageCount == 1, worker);
                Assert.Equal(3, processor.Attempts);
                await using (var before = CreateContext())
                {
                    Assert.False(await before.InboxMessages.AnyAsync(item => item.Id == messageId));
                }
                var failed = await channel.BasicGetAsync(CoreBanking.Worker.TransferQueues.Failed, autoAck: false);
                Assert.NotNull(failed);
                Assert.True(failed.BasicProperties.IsTimestampPresent());
                Assert.True(failed.BasicProperties.Timestamp.UnixTime > 0);
                Assert.True(failed.BasicProperties.Headers!.TryGetValue("traceparent", out var failedParent));
                Assert.True(failedParent is byte[]);
                Assert.StartsWith($"00-{traceId}-", Encoding.ASCII.GetString((byte[])failedParent));
                await channel.BasicNackAsync(failed.DeliveryTag, multiple: false, requeue: true);
                Assert.True(await CoreBanking.Worker.TransferQueues.ReprocessNextAsync(channel, CancellationToken.None));
            }

            await WaitForAsync(async () =>
            {
                await using var db = CreateContext();
                return await db.InboxMessages.AnyAsync(item => item.Id == messageId);
            }, worker);
            await WaitForAsync(() => Task.FromResult(spans.Count(item =>
                item.TraceId == traceId && item.OperationName == "transfer process") == failures + 1), worker);
            var traceSpans = spans.Where(item => item.TraceId == traceId).ToArray();
            var producer = Assert.Single(traceSpans, item => item.OperationName == "transfer publish");
            Assert.Equal(parentId, producer.ParentSpanId);
            var consumers = traceSpans.Where(item => item.OperationName == "transfer process").ToArray();
            var retries = traceSpans.Where(item => item.OperationName == "transfer retry publish").ToArray();
            Assert.Equal(failures, retries.Length);
            Assert.Equal(producer.SpanId, consumers[0].ParentSpanId);
            for (var index = 0; index < failures; index++)
            {
                Assert.Equal(consumers[index].SpanId, retries[index].ParentSpanId);
                Assert.Equal(retries[index].SpanId, consumers[index + 1].ParentSpanId);
            }
            Assert.True(metrics.Durations.Count(item => item.Name == "corebanking.rabbitmq.delivery.age") >= failures + 1);
            Assert.Contains(metrics.Durations, item => item.Name == "corebanking.rabbitmq.delivery.age" && item.Value >= 2);
            Assert.Equal(failures + 1, processor.Attempts);
            var times = processor.AttemptTimes.ToArray();
            Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(900));
            Assert.True(times[2] - times[1] >= TimeSpan.FromMilliseconds(1900));
            await using var verification = CreateContext();
            Assert.Equal(1, await verification.InboxMessages.CountAsync(item => item.Id == messageId));
            Assert.Equal(0u, (await channel.QueueDeclarePassiveAsync(CoreBanking.Worker.TransferQueues.Failed)).MessageCount);
            Assert.False(await CoreBanking.Worker.TransferQueues.ReprocessNextAsync(channel, CancellationToken.None));
            Assert.Contains(metrics.Counts, item => item.Name == "corebanking.transfer.messages" && item.Tag == "retry");
            if (failures == 3)
                Assert.Contains(metrics.Counts, item => item.Name == "corebanking.transfer.messages" && item.Tag == "failed");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await services.DisposeAsync();
        }
    }

    private sealed class FailingProcessor(int failures, string connectionString) : CoreBanking.Worker.ITransferEventProcessor
    {
        private int attempts;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public ConcurrentQueue<TimeSpan> AttemptTimes { get; } = new();
        public int Attempts => Volatile.Read(ref attempts);

        public async Task ProcessAsync(Guid messageId, Guid transferId, CancellationToken cancellationToken)
        {
            AttemptTimes.Enqueue(clock.Elapsed);
            if (Interlocked.Increment(ref attempts) <= failures)
            {
                throw new CoreBanking.Worker.MessageProcessingException("Falha simulada.");
            }

            await using var db = new BankingDbContext(
                new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(connectionString).Options);
            await new CoreBanking.Worker.TransferEventProcessor(db).ProcessAsync(messageId, transferId, cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger<CoreBanking.Worker.Worker>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, CoreBanking.Worker.Worker worker)
    {
        var timeout = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < timeout)
        {
            if (worker.ExecuteTask is { IsFaulted: true })
            {
                await worker.ExecuteTask;
            }

            if (await condition()) return;
            await Task.Delay(250);
        }

        throw new TimeoutException("O worker não concluiu o processamento esperado.");
    }
}
