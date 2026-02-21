---
status: pending
parallelizable: true
blocked_by: ["9.0"]
---

<task_context>
<domain>engine/application</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>high</complexity>
<dependencies>database, rabbitmq</dependencies>
<unblocks>"16.0"</unblocks>
</task_context>

# Tarefa 11.0: Detecção e Limpeza de Dados Órfãos (F06)

## Visão Geral

Implementar o mecanismo de detecção e limpeza de dados órfãos para garantir zero inconsistências entre o storage e o PostgreSQL. Inclui um job periódico (IHostedService) que detecta uploads `Pending` por mais de 24h, publica eventos de timeout, e um consumer na DLQ que reconcilia ou limpa dados. No cenário MinIO, lifecycle rules expiram multipart incompletos após 3 dias. No cenário TUS, arquivos no disco sem registro no PostgreSQL são removidos.

**Premissa:** Cada arquivo vale $1 milhão. Dados órfãos são inaceitáveis.

## Requisitos

- RF06.1: Uploads `Pending` por mais de 24h detectados por job periódico.
- RF06.2: Job publica `upload.timeout` na DLQ.
- RF06.3: Consumer da DLQ verifica storage e reconcilia ou limpa.
- RF06.4: Lifecycle rules no MinIO expiram multipart incompletos após 3 dias.
- RF06.5: Arquivos TUS no disco sem registro no PostgreSQL são removidos.
- RF06.6: Configuração automatizável (startup).
- RF06.7: Toda ação de limpeza logada para auditoria.

## Subtarefas

- [ ] 11.1 Criar `OrphanCleanupJob` em `2-Application/UploadPoc.Application/Jobs/OrphanCleanupJob.cs`:
  - Implementar como `BackgroundService` (`IHostedService`)
  - Timer configurável via `OrphanCleanup:IntervalMinutes` (padrão: 60 min)
  - A cada execução:
    1. Buscar uploads `Pending` mais antigos que `OrphanCleanup:TimeoutHours` (padrão: 24h)
    2. Para cada upload antigo: publicar `upload.timeout` via `IEventPublisher.PublishUploadTimeoutAsync`
    3. Buscar arquivos TUS no disco sem registro no PostgreSQL e removê-los
  - Logar cada ação: `{ uploadId, action, storageKey, timestamp }`
- [ ] 11.2 Implementar detecção de arquivos TUS órfãos no disco:
  - Listar todos os arquivos em `TusStorage:Path`
  - Para cada arquivo (excluindo metadados .metadata, .uploadlength, etc.), verificar se existe registro no PostgreSQL com `StorageKey` correspondente
  - Se não existe: remover arquivo e metadados associados
  - Logar: `"Órfão removido: { storageKey, action: 'deleted_from_disk' }"`
- [ ] 11.3 Implementar consumer da DLQ (`upload-completed-dlq`):
  - Consumir mensagens na `upload-completed-dlq`
  - Para cada mensagem, verificar se o arquivo existe no storage:
    - Se existe e está completo → tentar verificar SHA-256 → se ok, publicar novo `upload.completed`
    - Se não existe ou está incompleto → atualizar status para `Failed` e remover dados parciais
  - Manual ack após processamento
- [ ] 11.4 Para cenário MinIO — abort de multipart uploads órfãos:
  - No consumer da DLQ, se `UploadScenario == "MINIO"` e `MinioUploadId` não null:
    - Chamar `AbortMultipartUploadAsync` para limpar chunks
  - Lifecycle rules já configuradas na tarefa 7.0 (3 dias) como safety net
- [ ] 11.5 Implementar `PublishUploadTimeoutAsync` no `RabbitMqPublisher`:
  - Publicar diretamente na DLQ (`upload-events-dlx` com routing key correspondente)
  - Payload: `{ uploadId, timestamp }`
- [ ] 11.6 Registrar `OrphanCleanupJob` como HostedService no DI
- [ ] 11.7 Adicionar seção de configuração `OrphanCleanup` no `appsettings.json`
- [ ] 11.8 Testar cenários:
  - Upload `Pending` por mais de threshold → job detecta e publishes timeout
  - Arquivo TUS no disco sem registro → removido pelo job
  - Mensagem na DLQ → consumer processa e reconcilia ou limpa

## Sequenciamento

- Bloqueado por: 9.0 (Consumer de Integridade — reutiliza infra de RabbitMQ e storage services)
- Desbloqueia: 16.0 (K8s YAMLs — job precisa estar funcional antes de empacotar)
- Paralelizável: Sim (pode ser feito em paralelo com 10.0 — Listagem e Download)

## Detalhes de Implementação

### OrphanCleanupJob

```csharp
public class OrphanCleanupJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OrphanCleanupJob> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly string _tusStoragePath;

    public OrphanCleanupJob(IServiceProvider serviceProvider,
                            IConfiguration config,
                            ILogger<OrphanCleanupJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(
            int.Parse(config["OrphanCleanup:IntervalMinutes"] ?? "60"));
        _timeout = TimeSpan.FromHours(
            int.Parse(config["OrphanCleanup:TimeoutHours"] ?? "24"));
        _tusStoragePath = config["TusStorage:Path"] ?? "/app/uploads";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DetectTimeoutUploads(stoppingToken);
                await DetectTusOrphans(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no job de limpeza de órfãos");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task DetectTimeoutUploads(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFileUploadRepository>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var pendingUploads = await repo.GetPendingOlderThanAsync(_timeout, ct);

        foreach (var upload in pendingUploads)
        {
            _logger.LogWarning("Upload timeout detectado: {UploadId} pendente há {Hours}h",
                upload.Id, (DateTime.UtcNow - upload.CreatedAt).TotalHours);

            await publisher.PublishUploadTimeoutAsync(upload.Id, ct);
        }

        if (pendingUploads.Count > 0)
            _logger.LogInformation("Job de limpeza: {Count} uploads em timeout detectados",
                pendingUploads.Count);
    }

    private async Task DetectTusOrphans(CancellationToken ct)
    {
        if (!Directory.Exists(_tusStoragePath)) return;

        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFileUploadRepository>();

        var files = Directory.GetFiles(_tusStoragePath)
            .Where(f => !f.Contains('.')) // Arquivos TUS não têm extensão
            .ToList();

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            // Verificar se existe registro no PostgreSQL com este StorageKey
            // Nota: pode precisar de método adicional no repo (GetByStorageKey)
            var allUploads = await repo.GetAllAsync(ct);
            var hasRecord = allUploads.Any(u => u.StorageKey == fileName);

            if (!hasRecord)
            {
                File.Delete(filePath);
                // Remover metadados TUS associados
                foreach (var metaFile in Directory.GetFiles(_tusStoragePath, $"{fileName}.*"))
                    File.Delete(metaFile);

                _logger.LogInformation("Órfão removido: {StorageKey} action=deleted_from_disk",
                    fileName);
            }
        }
    }
}
```

### Consumer da DLQ

```csharp
// Dentro de um second BackgroundService ou extensão do RabbitMqConsumerHostedService:
// Consumir "upload-completed-dlq"

private async Task HandleDlqMessage(UploadCompletedEvent? evt, Guid? timeoutUploadId,
    CancellationToken ct)
{
    using var scope = _serviceProvider.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<IFileUploadRepository>();

    var uploadId = evt?.UploadId ?? timeoutUploadId!.Value;
    var upload = await repo.GetByIdAsync(uploadId, ct);
    if (upload == null) return;

    // Verificar no storage
    var storage = upload.UploadScenario == "TUS"
        ? scope.ServiceProvider.GetRequiredKeyedService<IStorageService>("tus")
        : scope.ServiceProvider.GetRequiredKeyedService<IStorageService>("minio");

    if (upload.StorageKey != null && await storage.ExistsAsync(upload.StorageKey, ct))
    {
        // Arquivo existe — tentar reconciliar
        var sha256 = await storage.ComputeSha256Async(upload.StorageKey, ct);
        if (string.Equals(sha256, upload.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            upload.MarkCompleted(sha256, upload.StorageKey);
            _logger.LogInformation("Upload reconciliado via DLQ: {UploadId}", uploadId);
        }
        else
        {
            upload.MarkCorrupted(sha256);
            _logger.LogWarning("Upload corrompido detectado via DLQ: {UploadId}", uploadId);
        }
    }
    else
    {
        // Arquivo não existe ou incompleto — marcar como Failed
        upload.MarkFailed("Timeout — arquivo não encontrado no storage");

        // Limpar multipart incompleto no MinIO
        if (upload.UploadScenario == "MINIO" && upload.MinioUploadId != null)
        {
            var minioStorage = scope.ServiceProvider
                .GetRequiredKeyedService<IStorageService>("minio") as MinioStorageService;
            await minioStorage!.AbortMultipartAsync(
                upload.StorageKey!, upload.MinioUploadId, ct);
        }

        _logger.LogInformation("Upload marcado como Failed via DLQ: {UploadId} reason=timeout",
            uploadId);
    }

    await repo.UpdateAsync(upload, ct);
}
```

### Configuração appsettings.json

```json
{
  "OrphanCleanup": {
    "TimeoutHours": 24,
    "IntervalMinutes": 60
  }
}
```

## Critérios de Sucesso

- Job periódico executa a cada N minutos (configurável)
- Uploads `Pending` por mais de 24h são detectados e evento `upload.timeout` é publicado
- Arquivos TUS no disco sem registro no PostgreSQL são removidos automaticamente
- Consumer da DLQ processa mensagens:
  - Se arquivo completo no storage → reconcilia (status `Completed` ou `Corrupted`)
  - Se arquivo não existe → marca como `Failed` e limpa storage
- Multipart incompleto no MinIO é abortado quando upload entra em `Failed`
- Toda ação de limpeza é logada com `uploadId`, `action` e `storageKey`
- Job não falha silenciosamente (exceções são logadas)
- Configuração de timeout e intervalo são lidas do `appsettings.json`
