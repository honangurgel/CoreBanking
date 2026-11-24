using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoreBanking.Infrastructure;
using CoreBanking.Infrastructure.Messaging;
using CoreBanking.Infrastructure.Transfers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace CoreBanking.IntegrationTests;

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
            await channel.BasicPublishAsync("", "corebanking.transfers.completed", Encoding.UTF8.GetBytes("invalid json"));

            await WaitForAsync(async () =>
            {
                var result = await channel.QueueDeclarePassiveAsync("corebanking.transfers.failed");
                return result.MessageCount == 1;
            }, worker);
            await using var verification = CreateContext();
            Assert.Equal(1, await verification.InboxMessages.CountAsync(item => item.Id == messageId));
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
            db.OutboxMessages.Add(new OutboxMessage { Id = messageId, TransferId = transferId, CreatedAt = DateTimeOffset.UtcNow });
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
                Assert.True(await CoreBanking.Worker.TransferQueues.ReprocessNextAsync(channel, CancellationToken.None));
            }

            await WaitForAsync(async () =>
            {
                await using var db = CreateContext();
                return await db.InboxMessages.AnyAsync(item => item.Id == messageId);
            }, worker);
            Assert.Equal(failures + 1, processor.Attempts);
            var times = processor.AttemptTimes.ToArray();
            Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(900));
            Assert.True(times[2] - times[1] >= TimeSpan.FromMilliseconds(1900));
            await using var verification = CreateContext();
            Assert.Equal(1, await verification.InboxMessages.CountAsync(item => item.Id == messageId));
            Assert.Equal(0u, (await channel.QueueDeclarePassiveAsync(CoreBanking.Worker.TransferQueues.Failed)).MessageCount);
            Assert.False(await CoreBanking.Worker.TransferQueues.ReprocessNextAsync(channel, CancellationToken.None));
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
