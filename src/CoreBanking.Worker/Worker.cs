using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;
using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace CoreBanking.Worker;

public sealed class Worker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<Worker> logger) : BackgroundService
{
    private const string QueueName = "corebanking.transfers.completed";
    private const string DeadLetterQueueName = "corebanking.transfers.failed";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var uri = configuration["RabbitMQ:ConnectionString"]
            ?? throw new InvalidOperationException("Configure RabbitMQ:ConnectionString.");
        var factory = new ConnectionFactory { Uri = new Uri(uri) };
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAsync(factory, () => failures = 0, stoppingToken);
            }
            catch (Exception exception) when (exception is RabbitMQClientException or IOException or SocketException or TimeoutException or DbException)
            {
                failures = Math.Min(failures + 1, 6);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, failures));
                logger.LogError(exception, "Falha temporária no worker; nova tentativa em {Delay}.", delay);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(ConnectionFactory factory, Action resetFailures, CancellationToken stoppingToken)
    {
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
        await channel.QueueDeclareAsync(DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = DeadLetterQueueName
            }, cancellationToken: stoppingToken);

        logger.LogInformation("Worker de eventos iniciado.");
        while (!stoppingToken.IsCancellationRequested)
        {
            var published = await PublishNextAsync(channel, stoppingToken);
            var consumed = await ConsumeNextAsync(channel, stoppingToken);
            resetFailures();
            if (!published && !consumed)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task<bool> PublishNextAsync(IChannel channel, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var message = await db.OutboxMessages.FromSqlRaw("""
            SELECT * FROM "OutboxMessages"
            WHERE "PublishedAt" IS NULL ORDER BY "CreatedAt", "Id"
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(cancellationToken);
        if (message is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new TransferCompleted(message.Id, message.TransferId));
        var properties = new BasicProperties { Persistent = true, MessageId = message.Id.ToString(), ContentType = "application/json" };
        await channel.BasicPublishAsync("", QueueName, mandatory: true, basicProperties: properties, body: body, cancellationToken: cancellationToken);
        message.PublishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ConsumeNextAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var delivery = await channel.BasicGetAsync(QueueName, autoAck: false, cancellationToken);
        if (delivery is null)
        {
            return false;
        }

        TransferCompleted? message;
        try
        {
            message = JsonSerializer.Deserialize<TransferCompleted>(delivery.Body.Span);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Mensagem inválida enviada para a fila de falhas.");
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
            return true;
        }

        if (message is null || message.MessageId == Guid.Empty || message.TransferId == Guid.Empty)
        {
            logger.LogError("Mensagem sem identificadores válidos enviada para a fila de falhas.");
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
            return true;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var known = await db.OutboxMessages.AsNoTracking().AnyAsync(
            item => item.Id == message.MessageId && item.TransferId == message.TransferId, cancellationToken);
        if (!known)
        {
            logger.LogError("Mensagem {MessageId} não corresponde à Outbox e foi enviada para a fila de falhas.", message.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
            return true;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InboxMessages" ("Id", "TransferId", "ReceivedAt")
            VALUES ({message.MessageId}, {message.TransferId}, {DateTimeOffset.UtcNow})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        return true;
    }

    private sealed record TransferCompleted(Guid MessageId, Guid TransferId);
}
