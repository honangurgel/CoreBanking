using RabbitMQ.Client;

namespace CoreBanking.Worker;

public static class TransferQueues
{
    public const string Main = "corebanking.transfers.completed";
    public const string Failed = "corebanking.transfers.failed";
    public const string FirstRetry = "corebanking.transfers.retry.1";
    public const string SecondRetry = "corebanking.transfers.retry.2";
    public const string AttemptHeader = "corebanking-attempt";

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(Failed, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(Main, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = Failed
            }, cancellationToken: cancellationToken);
        await DeclareRetryAsync(channel, FirstRetry, 1000, cancellationToken);
        await DeclareRetryAsync(channel, SecondRetry, 2000, cancellationToken);
    }

    private static async Task DeclareRetryAsync(IChannel channel, string name, int delayMilliseconds, CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
                ["x-message-ttl"] = delayMilliseconds,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = Main
            }, cancellationToken: cancellationToken);
    }

    public static async Task<bool> ReprocessNextAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var delivery = await channel.BasicGetAsync(Failed, autoAck: false, cancellationToken);
        if (delivery is null)
        {
            return false;
        }

        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = delivery.BasicProperties.MessageId,
            ContentType = delivery.BasicProperties.ContentType
        };
        await channel.BasicPublishAsync("", Main, mandatory: true, basicProperties: properties,
            body: delivery.Body, cancellationToken: cancellationToken);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        return true;
    }
}
