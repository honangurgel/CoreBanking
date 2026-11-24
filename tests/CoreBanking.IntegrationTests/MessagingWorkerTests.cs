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

        var services = new ServiceCollection()
            .AddDbContext<BankingDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()))
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
