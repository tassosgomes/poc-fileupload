using RabbitMQ.Client;

namespace UploadPoc.Infra.Messaging;

internal static class RabbitMqInfrastructureSetup
{
    internal const string UploadEventsExchange = "upload-events";
    internal const string DeadLetterExchange = "upload-events-dlx";
    internal const string UploadCompletedQueue = "upload-completed-queue";
    internal const string UploadCompletedDeadLetterQueue = "upload-completed-dlq";
    internal const string UploadCompletedRoutingKey = "upload.completed";
    internal const string UploadTimeoutRoutingKey = "upload.timeout";

    internal static void Declare(IModel channel)
    {
        channel.ExchangeDeclare(UploadEventsExchange, ExchangeType.Direct, durable: true);
        channel.ExchangeDeclare(DeadLetterExchange, ExchangeType.Direct, durable: true);

        var queueArguments = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = DeadLetterExchange,
            ["x-dead-letter-routing-key"] = UploadCompletedRoutingKey
        };

        channel.QueueDeclare(
            queue: UploadCompletedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments);

        channel.QueueDeclare(
            queue: UploadCompletedDeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        channel.QueueBind(UploadCompletedQueue, UploadEventsExchange, UploadCompletedRoutingKey);
        channel.QueueBind(UploadCompletedDeadLetterQueue, DeadLetterExchange, UploadCompletedRoutingKey);
        channel.QueueBind(UploadCompletedDeadLetterQueue, DeadLetterExchange, UploadTimeoutRoutingKey);
    }
}
