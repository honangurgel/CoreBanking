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
        await TransferQueues.DeclareAsync(channel, stoppingToken);

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
        await channel.BasicPublishAsync("", TransferQueues.Main, mandatory: true, basicProperties: properties, body: body, cancellationToken: cancellationToken);
        message.PublishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ConsumeNextAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var delivery = await channel.BasicGetAsync(TransferQueues.Main, autoAck: false, cancellationToken);
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
            logger.LogError(exception, "Encaminhando mensagem inválida para a fila de falhas.");
            await MoveInvalidAsync(channel, delivery, cancellationToken);
            return true;
        }

        if (message is null || message.MessageId == Guid.Empty || message.TransferId == Guid.Empty)
        {
            logger.LogError("Encaminhando mensagem sem identificadores válidos para a fila de falhas.");
            await MoveInvalidAsync(channel, delivery, cancellationToken);
            return true;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var known = await db.OutboxMessages.AsNoTracking().AnyAsync(
            item => item.Id == message.MessageId && item.TransferId == message.TransferId, cancellationToken);
        if (!known)
        {
            logger.LogError("Mensagem {MessageId} não corresponde à Outbox; encaminhando para a fila de falhas.", message.MessageId);
            await MoveInvalidAsync(channel, delivery, cancellationToken);
            return true;
        }

        var attempt = 1;
        if (delivery.BasicProperties.Headers is not null
            && delivery.BasicProperties.Headers.TryGetValue(TransferQueues.AttemptHeader, out var value))
        {
            if (value is not int count || count is < 2 or > 3)
            {
                logger.LogError("Mensagem {MessageId} possui contador de tentativas inválido.", message.MessageId);
                await MoveInvalidAsync(channel, delivery, cancellationToken);
                return true;
            }
            attempt = count;
        }

        if (!await db.InboxMessages.AsNoTracking().AnyAsync(item => item.Id == message.MessageId, cancellationToken))
        {
            try
            {
                var processor = scope.ServiceProvider.GetRequiredService<ITransferEventProcessor>();
                await processor.ProcessAsync(message.MessageId, message.TransferId, cancellationToken);
            }
            catch (MessageProcessingException exception)
            {
                var destination = attempt switch
                {
                    1 => TransferQueues.FirstRetry,
                    2 => TransferQueues.SecondRetry,
                    _ => TransferQueues.Failed
                };
                logger.LogError(exception, "Falha ao processar mensagem {MessageId}, tentativa {Attempt}; destino {Destination}.",
                    message.MessageId, attempt, destination);
                var properties = new BasicProperties
                {
                    Persistent = true,
                    MessageId = message.MessageId.ToString(),
                    ContentType = "application/json",
                    Headers = new Dictionary<string, object?> { [TransferQueues.AttemptHeader] = attempt + 1 }
                };
                await channel.BasicPublishAsync("", destination, mandatory: true, basicProperties: properties,
                    body: delivery.Body, cancellationToken: cancellationToken);
            }
        }
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        return true;
    }

    private static async Task MoveInvalidAsync(IChannel channel, BasicGetResult delivery, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = delivery.BasicProperties.MessageId,
            ContentType = delivery.BasicProperties.ContentType,
            Headers = delivery.BasicProperties.Headers
        };
        await channel.BasicPublishAsync("", TransferQueues.Failed, mandatory: true, basicProperties: properties,
            body: delivery.Body, cancellationToken: cancellationToken);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
    }

    private sealed record TransferCompleted(Guid MessageId, Guid TransferId);
}
