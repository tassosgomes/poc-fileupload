using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UploadPoc.Application.Consumers;
using UploadPoc.Domain.Events;

namespace UploadPoc.Infra.Messaging;

public sealed class RabbitMqConsumerHostedService : BackgroundService
{
    private const int MaxDeliveryAttempts = 3;
    private const string DeliveryCountHeaderName = "x-delivery-count";

    private readonly ILogger<RabbitMqConsumerHostedService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _syncRoot = new();

    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqConsumerHostedService(
        IConfiguration configuration,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<RabbitMqConsumerHostedService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        var rabbitSection = configuration.GetSection("RabbitMQ");
        _options = new RabbitMqOptions
        {
            Host = rabbitSection["Host"] ?? "localhost",
            Port = int.TryParse(rabbitSection["Port"], out var port) ? port : 5672,
            Username = rabbitSection["Username"] ?? "guest",
            Password = rabbitSection["Password"] ?? "guest"
        };

        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                lock (_syncRoot)
                {
                    EnsureConnectionAndInfrastructure();
                }

                _logger.LogInformation("RabbitMQ consumer connected and waiting for messages.");

                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "RabbitMQ consumer is unavailable. Retrying connection in 5 seconds.");

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            _channel?.Dispose();
            _connection?.Dispose();
            _channel = null;
            _connection = null;
        }

        return base.StopAsync(cancellationToken);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("RabbitMQ channel is not initialized for consumer.");
        }

        var asyncConsumer = new AsyncEventingBasicConsumer(_channel);
        asyncConsumer.Received += async (_, eventArgs) => await HandleMessageAsync(eventArgs, cancellationToken);

        _channel.BasicConsume(
            queue: RabbitMqInfrastructureSetup.UploadCompletedQueue,
            autoAck: false,
            consumer: asyncConsumer);

        while (!cancellationToken.IsCancellationRequested && _channel.IsOpen && _connection?.IsOpen == true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task HandleMessageAsync(BasicDeliverEventArgs eventArgs, CancellationToken cancellationToken)
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            var uploadCompletedEvent = JsonSerializer.Deserialize<UploadCompletedEvent>(eventArgs.Body.Span, _serializerOptions);

            if (uploadCompletedEvent is null)
            {
                throw new InvalidOperationException("UploadCompletedEvent payload is null.");
            }

            using var scope = _serviceScopeFactory.CreateScope();
            var consumer = scope.ServiceProvider.GetRequiredService<UploadCompletedConsumer>();

            await consumer.ProcessAsync(uploadCompletedEvent, cancellationToken);

            _channel.BasicAck(eventArgs.DeliveryTag, multiple: false);
        }
        catch (Exception exception)
        {
            var currentAttempt = GetCurrentDeliveryAttempt(eventArgs.BasicProperties?.Headers);
            if (currentAttempt < MaxDeliveryAttempts)
            {
                var nextAttempt = currentAttempt + 1;

                try
                {
                    PublishRetryMessage(eventArgs, nextAttempt);
                    _channel.BasicAck(eventArgs.DeliveryTag, multiple: false);

                    _logger.LogWarning(
                        exception,
                        "Error processing upload.completed message. DeliveryTag={DeliveryTag}. Retrying attempt {NextAttempt}/{MaxDeliveryAttempts}.",
                        eventArgs.DeliveryTag,
                        nextAttempt,
                        MaxDeliveryAttempts);

                    return;
                }
                catch (Exception retryException)
                {
                    _logger.LogError(
                        retryException,
                        "Failed to enqueue retry for upload.completed message. DeliveryTag={DeliveryTag}.",
                        eventArgs.DeliveryTag);
                }
            }

            _logger.LogError(
                exception,
                "Error processing upload.completed message. DeliveryTag={DeliveryTag}. Max attempts reached ({MaxDeliveryAttempts}), sending to DLQ.",
                eventArgs.DeliveryTag,
                MaxDeliveryAttempts);

            _channel.BasicNack(eventArgs.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private void PublishRetryMessage(BasicDeliverEventArgs eventArgs, int nextAttempt)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("RabbitMQ channel is not initialized for retry publishing.");
        }

        var retryProperties = _channel.CreateBasicProperties();
        var originalProperties = eventArgs.BasicProperties;

        retryProperties.Persistent = originalProperties?.Persistent ?? true;
        retryProperties.ContentType = originalProperties?.ContentType ?? "application/json";
        retryProperties.ContentEncoding = originalProperties?.ContentEncoding;
        retryProperties.CorrelationId = originalProperties?.CorrelationId;
        retryProperties.MessageId = originalProperties?.MessageId;
        retryProperties.Type = originalProperties?.Type;
        retryProperties.AppId = originalProperties?.AppId;
        retryProperties.Timestamp = originalProperties?.Timestamp ?? default;
        retryProperties.Headers = CloneHeaders(originalProperties?.Headers);
        retryProperties.Headers[DeliveryCountHeaderName] = nextAttempt;

        _channel.BasicPublish(
            exchange: RabbitMqInfrastructureSetup.UploadEventsExchange,
            routingKey: RabbitMqInfrastructureSetup.UploadCompletedRoutingKey,
            mandatory: false,
            basicProperties: retryProperties,
            body: eventArgs.Body);
    }

    private static Dictionary<string, object> CloneHeaders(IDictionary<string, object>? headers)
    {
        var clonedHeaders = new Dictionary<string, object>(StringComparer.Ordinal);
        if (headers is null)
        {
            return clonedHeaders;
        }

        foreach (var (key, value) in headers)
        {
            clonedHeaders[key] = value;
        }

        return clonedHeaders;
    }

    private static int GetCurrentDeliveryAttempt(IDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue(DeliveryCountHeaderName, out var rawValue))
        {
            return 1;
        }

        return ParseDeliveryAttempt(rawValue);
    }

    private static int ParseDeliveryAttempt(object value)
    {
        var parsedAttempt = value switch
        {
            byte byteValue => byteValue,
            sbyte sbyteValue => sbyteValue,
            short shortValue => shortValue,
            ushort ushortValue => ushortValue,
            int intValue => intValue,
            uint uintValue => (int)uintValue,
            long longValue => (int)longValue,
            ulong ulongValue => (int)ulongValue,
            string stringValue when int.TryParse(stringValue, out var stringAttempt) => stringAttempt,
            byte[] bytesValue when int.TryParse(Encoding.UTF8.GetString(bytesValue), out var bytesAttempt) => bytesAttempt,
            _ => 1
        };

        return parsedAttempt < 1 ? 1 : parsedAttempt;
    }

    private void EnsureConnectionAndInfrastructure()
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
        {
            return;
        }

        _channel?.Dispose();
        _connection?.Dispose();
        _channel = null;
        _connection = null;

        var connectionFactory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.Username,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = connectionFactory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        RabbitMqInfrastructureSetup.Declare(_channel);
    }
}
