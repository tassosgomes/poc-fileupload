using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using RabbitMQ.Client;
using UploadPoc.Domain.Events;
using UploadPoc.Domain.Interfaces;

namespace UploadPoc.Infra.Messaging;

public sealed class RabbitMqPublisher : IEventPublisher, IDisposable
{
    private const int MaxPublishAttempts = 3;
    private static readonly TimeSpan PublishConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly RabbitMqOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly object _syncRoot = new();

    private IConnection? _connection;
    private IModel? _channel;
    private bool _disposed;

    public RabbitMqPublisher(IConfiguration configuration, ILogger<RabbitMqPublisher> logger)
    {
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
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        _retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: MaxPublishAttempts,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)),
                onRetry: (exception, delay, retryCount, _) =>
                {
                    _logger.LogWarning(
                        exception,
                        "Retry {RetryCount}/{MaxRetryCount} publishing event to RabbitMQ in {DelaySeconds}s",
                        retryCount,
                        MaxPublishAttempts,
                        delay.TotalSeconds);
                });

        TryInitializeInfrastructure();
    }

    public async Task PublishUploadCompletedAsync(UploadCompletedEvent @event, CancellationToken cancellationToken)
    {
        await PublishAsync(@event, RabbitMqInfrastructureSetup.UploadCompletedRoutingKey, cancellationToken);
    }

    public async Task PublishUploadTimeoutAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var timeoutPayload = new
        {
            uploadId,
            timestamp = DateTime.UtcNow
        };

        await PublishAsync(timeoutPayload, RabbitMqInfrastructureSetup.UploadTimeoutRoutingKey, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncRoot)
        {
            _channel?.Dispose();
            _connection?.Dispose();
            _channel = null;
            _connection = null;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task PublishAsync(object payload, string routingKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _retryPolicy.ExecuteAsync(async ct =>
        {
            PublishInternal(payload, routingKey, ct);
            await Task.CompletedTask;
        }, cancellationToken);
    }

    private void PublishInternal(object payload, string routingKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            EnsureConnectionAndInfrastructure();

            if (_channel is null)
            {
                throw new InvalidOperationException("RabbitMQ channel is not available for publishing.");
            }

            var body = JsonSerializer.SerializeToUtf8Bytes(payload, _serializerOptions);
            var properties = _channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";

            _channel.BasicPublish(
                exchange: RabbitMqInfrastructureSetup.UploadEventsExchange,
                routingKey: routingKey,
                mandatory: true,
                basicProperties: properties,
                body: body);

            _channel.WaitForConfirmsOrDie(PublishConfirmTimeout);
        }
    }

    private void TryInitializeInfrastructure()
    {
        try
        {
            lock (_syncRoot)
            {
                EnsureConnectionAndInfrastructure();
            }

            _logger.LogInformation("RabbitMQ publisher initialized successfully.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "RabbitMQ is unavailable on startup. Publisher will retry lazily on publish attempts.");
        }
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
        _channel.ConfirmSelect();

        RabbitMqInfrastructureSetup.Declare(_channel);
    }
}
