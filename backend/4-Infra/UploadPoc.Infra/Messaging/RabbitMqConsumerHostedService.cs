using System.Text;
using System.Text.Json;
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
    private const int MaxDeliveryCount = 3;

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

                EnsureIntegrityHandlerRegistered();

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
            var payload = Encoding.UTF8.GetString(eventArgs.Body.ToArray());
            var uploadCompletedEvent = JsonSerializer.Deserialize<UploadCompletedEvent>(payload, _serializerOptions);

            if (uploadCompletedEvent is null)
            {
                throw new InvalidOperationException("UploadCompletedEvent payload is null.");
            }

            using var scope = _serviceScopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IIntegrityCheckHandler>();

            await handler.HandleAsync(uploadCompletedEvent, cancellationToken);

            _channel.BasicAck(eventArgs.DeliveryTag, multiple: false);
        }
        catch (Exception exception)
        {
            var deliveryCount = GetDeliveryCount(eventArgs.BasicProperties);

            _logger.LogError(
                exception,
                "Error processing upload.completed message. DeliveryCount={DeliveryCount}.",
                deliveryCount);

            if (deliveryCount < MaxDeliveryCount)
            {
                RepublishWithIncrementedDeliveryCount(eventArgs, deliveryCount + 1);
                _channel.BasicAck(eventArgs.DeliveryTag, multiple: false);
                return;
            }

            _channel.BasicNack(eventArgs.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private void EnsureIntegrityHandlerRegistered()
    {
        using var scope = _serviceScopeFactory.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<IIntegrityCheckHandler>();
    }

    private static int GetDeliveryCount(IBasicProperties? basicProperties)
    {
        if (basicProperties?.Headers is null || !basicProperties.Headers.TryGetValue("x-delivery-count", out var value))
        {
            return 1;
        }

        return value switch
        {
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            int intValue => intValue,
            long longValue => (int)longValue,
            _ => 1
        };
    }

    private void RepublishWithIncrementedDeliveryCount(BasicDeliverEventArgs eventArgs, int deliveryCount)
    {
        if (_channel is null)
        {
            return;
        }

        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        properties.Headers = new Dictionary<string, object>
        {
            ["x-delivery-count"] = deliveryCount.ToString()
        };

        _channel.BasicPublish(
            exchange: RabbitMqInfrastructureSetup.UploadEventsExchange,
            routingKey: RabbitMqInfrastructureSetup.UploadCompletedRoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: eventArgs.Body);
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
