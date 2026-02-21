---
status: pending
parallelizable: true
blocked_by: ["3.0"]
---

<task_context>
<domain>infra/messaging</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>high</complexity>
<dependencies>rabbitmq</dependencies>
<unblocks>"9.0"</unblocks>
</task_context>

# Tarefa 4.0: Camada Infra — RabbitMQ (Publisher, Consumer, DLQ)

## Visão Geral

Implementar a infraestrutura de mensageria com RabbitMQ usando a biblioteca `RabbitMQ.Client` (v6.x). Inclui o publisher com confirmação (publisher confirms), o consumer como `BackgroundService` com acknowledgment manual, e a configuração completa de exchange, queues e dead-letter queue (DLQ).

O RabbitMQ é o mecanismo central para desacoplar a conclusão do upload da validação de integridade (SHA-256), garantindo resiliência com DLQ para mensagens falhadas.

## Requisitos

- RF02.6: Eventos devem ser publicados com publisher confirms.
- RF02.7: Consumer deve usar acknowledgment manual — só ack após persistir no PostgreSQL.
- RF02.8: Mensagens falhadas devem ir para dead-letter queue.
- Configuração de exchange, queues e DLQ deve ser automática no startup.

## Subtarefas

- [ ] 4.1 Criar `RabbitMqPublisher` em `4-Infra/UploadPoc.Infra/Messaging/RabbitMqPublisher.cs`:
  - Implementar `IEventPublisher`
  - Singleton com connection e channel reutilizáveis
  - `ConfirmSelect()` para habilitar publisher confirms
  - `WaitForConfirmsOrDie()` após cada publicação
  - Retry com Polly (3 tentativas, backoff exponencial) em caso de falha
  - Serializar payload como JSON (System.Text.Json)
  - `PublishUploadCompletedAsync` → routing key `upload.completed`
  - `PublishUploadTimeoutAsync` → routing key `upload.timeout`
- [ ] 4.2 Criar `RabbitMqConsumerHostedService` em `4-Infra/UploadPoc.Infra/Messaging/RabbitMqConsumerHostedService.cs`:
  - `BackgroundService` que consome a queue `upload-completed-queue`
  - Manual ack: `BasicAck` somente após processamento bem-sucedido
  - Manual nack: `BasicNack(requeue: false)` para enviar à DLQ após falha
  - Controle de retry via header `x-delivery-count` (máx. 3 tentativas)
  - Deserializar payload JSON para `UploadCompletedEvent`
  - Delegar processamento para a camada Application (injetar handler via DI)
- [ ] 4.3 Criar método de setup de infraestrutura RabbitMQ (via startup ou método dedicado):
  - Declarar exchange `upload-events` (type: direct, durable: true)
  - Declarar dead-letter exchange `upload-events-dlx` (type: direct, durable: true)
  - Declarar queue `upload-completed-queue` (durable, arguments: `x-dead-letter-exchange: upload-events-dlx`)
  - Declarar queue `upload-completed-dlq` (durable, binding para `upload-events-dlx`)
  - Bind `upload-completed-queue` com routing key `upload.completed`
- [ ] 4.4 Registrar `IEventPublisher` como Singleton e `RabbitMqConsumerHostedService` como HostedService no DI
- [ ] 4.5 Criar seção de configuração `RabbitMQ` no `appsettings.json` (Host, Port, Username, Password)
- [ ] 4.6 Testar que exchanges e queues são criados no RabbitMQ Management UI (http://localhost:15672)

## Sequenciamento

- Bloqueado por: 3.0 (PostgreSQL — consumer precisa do repositório para atualizar status)
- Desbloqueia: 9.0 (Consumer de Integridade SHA-256)
- Paralelizável: Sim (pode ser feito em paralelo com 5.0 — Auth JWT)

## Detalhes de Implementação

### Configuração RabbitMQ

```
Exchange: upload-events (direct, durable)
  └─ Binding: upload.completed → upload-completed-queue

DLX Exchange: upload-events-dlx (direct, durable)
  └─ Binding: upload.completed → upload-completed-dlq

Queue: upload-completed-queue (durable)
  - x-dead-letter-exchange: upload-events-dlx
  - x-dead-letter-routing-key: upload.completed

Queue: upload-completed-dlq (durable)
```

### RabbitMqPublisher

```csharp
public class RabbitMqPublisher : IEventPublisher, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly ILogger<RabbitMqPublisher> _logger;

    public RabbitMqPublisher(IConfiguration config, ILogger<RabbitMqPublisher> logger)
    {
        _logger = logger;
        var factory = new ConnectionFactory
        {
            HostName = config["RabbitMQ:Host"],
            Port = int.Parse(config["RabbitMQ:Port"] ?? "5672"),
            UserName = config["RabbitMQ:Username"],
            Password = config["RabbitMQ:Password"]
        };
        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.ConfirmSelect(); // Publisher confirms

        // Declarar exchanges e queues
        SetupInfrastructure();
    }

    private void SetupInfrastructure()
    {
        // Exchange principal
        _channel.ExchangeDeclare("upload-events", ExchangeType.Direct, durable: true);

        // Dead-letter exchange
        _channel.ExchangeDeclare("upload-events-dlx", ExchangeType.Direct, durable: true);

        // Queue principal com DLX
        var args = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", "upload-events-dlx" },
            { "x-dead-letter-routing-key", "upload.completed" }
        };
        _channel.QueueDeclare("upload-completed-queue", durable: true, exclusive: false,
                              autoDelete: false, arguments: args);
        _channel.QueueBind("upload-completed-queue", "upload-events", "upload.completed");

        // Dead-letter queue
        _channel.QueueDeclare("upload-completed-dlq", durable: true, exclusive: false,
                              autoDelete: false, arguments: null);
        _channel.QueueBind("upload-completed-dlq", "upload-events-dlx", "upload.completed");
    }

    public async Task PublishUploadCompletedAsync(UploadCompletedEvent evt, CancellationToken ct)
    {
        // Serializar, publicar, confirmar (com retry via Polly)
    }

    public async Task PublishUploadTimeoutAsync(Guid uploadId, CancellationToken ct)
    {
        // Publicar na DLQ diretamente
    }
}
```

### Payload JSON

```json
{
  "uploadId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "storageKey": "uploads/file.dat",
  "expectedSha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  "uploadScenario": "TUS",
  "timestamp": "2026-02-21T10:30:00Z"
}
```

### Retry com Polly

```csharp
var retryPolicy = Policy
    .Handle<Exception>()
    .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
        (exception, timeSpan, retryCount, context) =>
        {
            _logger.LogWarning("Retry {RetryCount} publicando evento no RabbitMQ: {Error}",
                retryCount, exception.Message);
        });
```

## Critérios de Sucesso

- Exchanges `upload-events` e `upload-events-dlx` aparecem no RabbitMQ Management UI
- Queues `upload-completed-queue` e `upload-completed-dlq` aparecem como durable
- Publisher confirms estão habilitados (canal confirma entrega)
- Mensagem publicada na queue principal é consumida pelo `RabbitMqConsumerHostedService`
- Mensagem com nack vai para `upload-completed-dlq`
- Log de erro contém informações do retry (tentativa, erro)
- A aplicação inicia sem erro mesmo se o RabbitMQ não estiver disponível (graceful degradation com retry no startup)
