---
status: pending
parallelizable: false
blocked_by: ["7.0", "8.0"]
---

<task_context>
<domain>engine/application</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>medium</complexity>
<dependencies>rabbitmq</dependencies>
<unblocks>"10.0", "11.0"</unblocks>
</task_context>

# Tarefa 9.0: Consumer de Integridade SHA-256 (F02 — Continuação)

## Visão Geral

Implementar o consumer RabbitMQ que processa o evento `upload.completed`, calcula o SHA-256 do arquivo no storage (disco para TUS, streaming via S3 para MinIO), compara com o hash esperado (calculado pelo frontend) e atualiza o status no PostgreSQL para `Completed` ou `Corrupted`.

Este consumer é o componente responsável pela **validação de integridade end-to-end** — garantindo que nenhum byte foi corrompido durante o upload.

## Requisitos

- RF02.4: Consumer processa `upload.completed`, valida integridade e atualiza status.
- RF02.5: SHA-256 divergente → status `Corrupted`.
- RF02.7: Ack manual somente após persistir atualização no PostgreSQL.
- RF02.8: Falhas vão para DLQ.

## Subtarefas

- [ ] 9.1 Criar `UploadCompletedConsumer` em `2-Application/UploadPoc.Application/Consumers/UploadCompletedConsumer.cs`:
  - Recebe `UploadCompletedEvent` deserializado
  - Determina o `IStorageService` correto com base em `UploadScenario` ("TUS" ou "MINIO")
  - Calcula SHA-256 do arquivo no storage via `IStorageService.ComputeSha256Async(storageKey)`
  - Compara `actualSha256` com `expectedSha256`
  - Se match: `upload.MarkCompleted(actualSha256, storageKey)`
  - Se mismatch: `upload.MarkCorrupted(actualSha256)` + log WARN
  - Persiste atualização via `IFileUploadRepository.UpdateAsync`
- [ ] 9.2 Criar `Sha256ChecksumService` em `4-Infra/UploadPoc.Infra/Services/Sha256ChecksumService.cs`:
  - Implementar `IChecksumService`
  - Calcular SHA-256 via streaming (`IncrementalHash.CreateHash(HashAlgorithmName.SHA256)`)
  - Ler em blocos de 8 KB para não consumir memória
  - Retornar hash como string hexadecimal lowercase
- [ ] 9.3 Integrar `UploadCompletedConsumer` no `RabbitMqConsumerHostedService`:
  - Quando mensagem chega na queue, deserializar para `UploadCompletedEvent`
  - Criar scope DI para resolver `UploadCompletedConsumer`
  - Chamar `ProcessAsync(event, ct)`
  - Se sucesso: `BasicAck`
  - Se exceção: logar erro, verificar retry count, `BasicNack(requeue: false)` para DLQ
- [ ] 9.4 Registrar `IChecksumService` e `UploadCompletedConsumer` no DI
- [ ] 9.5 Testar fluxo completo:
  - Upload TUS → evento → consumer → SHA-256 match → status `Completed`
  - Upload MinIO → evento → consumer → SHA-256 match → status `Completed`
  - Simular SHA-256 mismatch → status `Corrupted`

## Sequenciamento

- Bloqueado por: 7.0 (MinIO Backend — para ter `MinioStorageService` implementado), 8.0 (TUS Backend — para ter `TusDiskStorageService` implementado)
- Desbloqueia: 10.0 (Listagem e Download), 11.0 (Detecção de Órfãos)
- Paralelizável: Não (depende de 7.0 e 8.0 completos)

## Detalhes de Implementação

### UploadCompletedConsumer

```csharp
public class UploadCompletedConsumer
{
    private readonly IFileUploadRepository _repository;
    private readonly IStorageService _tusStorage;
    private readonly IStorageService _minioStorage;
    private readonly ILogger<UploadCompletedConsumer> _logger;

    public UploadCompletedConsumer(
        IFileUploadRepository repository,
        [FromKeyedServices("tus")] IStorageService tusStorage,
        [FromKeyedServices("minio")] IStorageService minioStorage,
        ILogger<UploadCompletedConsumer> logger)
    {
        _repository = repository;
        _tusStorage = tusStorage;
        _minioStorage = minioStorage;
        _logger = logger;
    }

    public async Task ProcessAsync(UploadCompletedEvent evt, CancellationToken ct)
    {
        var upload = await _repository.GetByIdAsync(evt.UploadId, ct)
            ?? throw new InvalidOperationException($"Upload {evt.UploadId} not found");

        // Selecionar storage correto
        var storage = evt.UploadScenario == "TUS" ? _tusStorage : _minioStorage;

        // Calcular SHA-256 do arquivo no storage
        _logger.LogInformation("Calculando SHA-256 para upload {UploadId} ({Scenario})",
            evt.UploadId, evt.UploadScenario);

        var actualSha256 = await storage.ComputeSha256Async(evt.StorageKey, ct);

        // Comparar com esperado
        if (string.Equals(actualSha256, evt.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            upload.MarkCompleted(actualSha256, evt.StorageKey);
            _logger.LogInformation("Upload concluído com integridade: {UploadId} SHA256={Sha256}",
                evt.UploadId, actualSha256);
        }
        else
        {
            upload.MarkCorrupted(actualSha256);
            _logger.LogWarning("SHA-256 mismatch para upload {UploadId}: esperado={Expected} atual={Actual}",
                evt.UploadId, evt.ExpectedSha256, actualSha256);
        }

        await _repository.UpdateAsync(upload, ct);
    }
}
```

### Sha256ChecksumService

```csharp
public class Sha256ChecksumService : IChecksumService
{
    public async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192]; // 8 KB buffer
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }

        var hashBytes = hash.GetHashAndReset();
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
```

### Integração no RabbitMqConsumerHostedService

```csharp
// Dentro do callback de consumo de mensagem:
private async Task HandleMessage(ReadOnlyMemory<byte> body, IModel channel,
    ulong deliveryTag, CancellationToken ct)
{
    try
    {
        var evt = JsonSerializer.Deserialize<UploadCompletedEvent>(body.Span);
        if (evt == null) throw new InvalidOperationException("Failed to deserialize event");

        using var scope = _serviceProvider.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<UploadCompletedConsumer>();
        await consumer.ProcessAsync(evt, ct);

        channel.BasicAck(deliveryTag, multiple: false);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Falha ao processar upload.completed: deliveryTag={DeliveryTag}", deliveryTag);
        channel.BasicNack(deliveryTag, multiple: false, requeue: false); // → DLQ
    }
}
```

### Registro DI com Keyed Services (.NET 8)

```csharp
// Para diferenciar TUS vs MinIO IStorageService:
builder.Services.AddKeyedScoped<IStorageService, TusDiskStorageService>("tus");
builder.Services.AddKeyedScoped<IStorageService, MinioStorageService>("minio");
builder.Services.AddScoped<IChecksumService, Sha256ChecksumService>();
builder.Services.AddScoped<UploadCompletedConsumer>();
```

## Critérios de Sucesso

- Consumer processa evento `upload.completed` da queue sem erro
- SHA-256 de arquivo TUS no disco é calculado corretamente (streaming, 8 KB buffer)
- SHA-256 de arquivo MinIO é calculado via `GetObjectAsync` streaming
- SHA-256 match → status atualizado para `Completed` no PostgreSQL
- SHA-256 mismatch → status atualizado para `Corrupted` no PostgreSQL
- Log WARN é emitido quando há mismatch com hashes esperado e actual
- Falha no processamento → mensagem vai para DLQ (manual nack, requeue: false)
- `BasicAck` é chamado somente após `UpdateAsync` com sucesso
- Fluxo end-to-end funciona: upload → evento → consumer → status final correto
