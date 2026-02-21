---
status: pending
parallelizable: false
blocked_by: ["6.0"]
---

<task_context>
<domain>engine/storage</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>high</complexity>
<dependencies>external_apis</dependencies>
<unblocks>"9.0"</unblocks>
</task_context>

# Tarefa 7.0: Upload MinIO — Backend (F04)

## Visão Geral

Implementar o cenário de upload via MinIO Multipart no backend. O backend apenas orquestra: inicia o multipart upload, gera pre-signed URLs para que o frontend envie bytes diretamente ao MinIO, e depois completa o multipart com os ETags retornados. Os bytes **não passam pelo backend**.

Este cenário é implementado primeiro por ser stateless (sem PVC compartilhado) e mais simples para validar o pipeline completo (registro → upload → evento → integridade).

## Requisitos

- RF04.1: Backend gera pre-signed URLs (uma por chunk de 100 MB).
- RF04.4: Backend recebe ETags e completa o multipart.
- RF04.5: Cancelamento chama `AbortMultipartUpload`.
- RF04.6: Pre-signed URLs com validade de 24h.
- RF04.8: Cancelamento atualiza status e aborta multipart.

## Subtarefas

- [ ] 7.1 Criar `MinioStorageService` em `4-Infra/UploadPoc.Infra/Storage/MinioStorageService.cs`:
  - Implementar `IStorageService` (para cenário MinIO)
  - Configurar `AmazonS3Client` com `ForcePathStyle = true` e `ServiceURL` do MinIO
  - `InitiateMultipartUploadAsync(bucketName, key)` → retorna `uploadId`
  - `GeneratePresignedUrls(bucketName, key, uploadId, totalParts)` → lista de URLs PUT (24h)
  - `CompleteMultipartUploadAsync(bucketName, key, uploadId, etags)` → consolida arquivo
  - `AbortMultipartUploadAsync(bucketName, key, uploadId)` → cancela multipart
  - `ComputeSha256Async(storageKey)` → streaming via `GetObjectAsync` + cálculo SHA-256
  - `DeleteAsync(storageKey)` → `DeleteObjectAsync`
  - `ExistsAsync(storageKey)` → `GetObjectMetadataAsync`
  - Retry com Polly (3 tentativas, backoff exponencial)
- [ ] 7.2 Criar `InitiateMinioUploadCommand` em `2-Application/UploadPoc.Application/Commands/InitiateMinioUploadCommand.cs`
- [ ] 7.3 Criar `InitiateMinioUploadHandler` em `2-Application/UploadPoc.Application/Handlers/InitiateMinioUploadHandler.cs`:
  - Validar metadados (reutilizar `RegisterUploadValidator`)
  - Criar entidade `FileUpload` com scenario "MINIO"
  - Calcular número de parts com base no tamanho (100 MB cada)
  - Chamar `InitiateMultipartUploadAsync` no MinIO
  - Gravar `StorageKey` e `MinioUploadId` na entidade
  - Persistir no PostgreSQL
  - Gerar pre-signed URLs
  - Retornar `InitiateMinioResponse` (uploadId, presignedUrls, partSize, totalParts)
- [ ] 7.4 Criar `CompleteUploadCommand` em `2-Application/UploadPoc.Application/Commands/CompleteUploadCommand.cs`
- [ ] 7.5 Criar `CompleteUploadHandler` em `2-Application/UploadPoc.Application/Handlers/CompleteUploadHandler.cs`:
  - Buscar upload por ID
  - Validar que está `Pending` e é cenário "MINIO"
  - Chamar `CompleteMultipartUploadAsync` com ETags
  - Publicar `UploadCompletedEvent` no RabbitMQ
  - Retornar sucesso
- [ ] 7.6 Criar `InitiateMinioResponse` em `2-Application/UploadPoc.Application/Dtos/InitiateMinioResponse.cs`
- [ ] 7.7 Criar `CompleteMinioRequest` em `2-Application/UploadPoc.Application/Dtos/CompleteMinioRequest.cs`
- [ ] 7.8 Criar `CompleteMinioValidator` em `2-Application/UploadPoc.Application/Validators/CompleteMinioValidator.cs`
- [ ] 7.9 Criar `MinioUploadController` em `1-Services/UploadPoc.API/Controllers/MinioUploadController.cs`:
  - `POST /api/v1/uploads/minio/initiate` → `InitiateMinioUploadHandler`
  - `POST /api/v1/uploads/minio/complete` → `CompleteUploadHandler`
  - `DELETE /api/v1/uploads/minio/abort` → `CancelUploadHandler` (com abort no MinIO)
- [ ] 7.10 Configurar CORS no MinIO via `minio-setup` no Docker Compose (AllowedOrigins, AllowedMethods PUT, ExposeHeaders ETag)
- [ ] 7.11 Configurar lifecycle rule no startup: expirar multipart incompletos após 3 dias (`PutLifecycleConfigurationAsync`)
- [ ] 7.12 Registrar `MinioStorageService` no DI (como named/keyed service ou interface separada)
- [ ] 7.13 Testar fluxo completo via Postman/curl: initiate → PUT manual → complete

## Sequenciamento

- Bloqueado por: 6.0 (Registro de Metadados — reutiliza commands/handlers/validators)
- Desbloqueia: 9.0 (Consumer de Integridade)
- Paralelizável: Não (8.0 depende do mesmo pipeline base, mas TUS é independente em implementação)

## Detalhes de Implementação

### MinioStorageService

```csharp
public class MinioStorageService : IStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly IChecksumService _checksumService;
    private readonly ILogger<MinioStorageService> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;

    public MinioStorageService(IConfiguration config, IChecksumService checksumService,
                               ILogger<MinioStorageService> logger)
    {
        var s3Config = new AmazonS3Config
        {
            ServiceURL = $"http://{config["MinIO:Endpoint"]}",
            ForcePathStyle = true,
            UseHttp = !bool.Parse(config["MinIO:UseSSL"] ?? "false")
        };
        _s3Client = new AmazonS3Client(
            config["MinIO:AccessKey"],
            config["MinIO:SecretKey"],
            s3Config);
        _bucketName = config["MinIO:BucketName"]!;

        _retryPolicy = Policy
            .Handle<AmazonS3Exception>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
    }

    public async Task<(string uploadId, List<string> presignedUrls)> InitiateMultipartAsync(
        string key, int totalParts, CancellationToken ct)
    {
        var initResponse = await _retryPolicy.ExecuteAsync(() =>
            _s3Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = key
            }, ct));

        var uploadId = initResponse.UploadId;
        var urls = new List<string>();

        for (int i = 1; i <= totalParts; i++)
        {
            var url = _s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.AddHours(24),
                PartNumber = i,
                UploadId = uploadId
            });
            urls.Add(url);
        }

        return (uploadId, urls);
    }

    public async Task CompleteMultipartAsync(string key, string uploadId,
        List<PartETag> etags, CancellationToken ct)
    {
        await _retryPolicy.ExecuteAsync(() =>
            _s3Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = key,
                UploadId = uploadId,
                PartETags = etags
            }, ct));
    }

    public async Task AbortMultipartAsync(string key, string uploadId, CancellationToken ct)
    {
        await _retryPolicy.ExecuteAsync(() =>
            _s3Client.AbortMultipartUploadAsync(_bucketName, key, uploadId, ct));
    }
}
```

### InitiateMinioResponse

```csharp
public record InitiateMinioResponse(
    Guid UploadId,
    string StorageKey,
    List<string> PresignedUrls,
    long PartSizeBytes,
    int TotalParts
);
```

### CompleteMinioRequest

```csharp
public record CompleteMinioRequest(
    Guid UploadId,
    List<PartETagDto> Parts
);

public record PartETagDto(int PartNumber, string ETag);
```

### MinioUploadController

```csharp
[ApiController]
[Route("api/v1/uploads/minio")]
[Authorize]
public class MinioUploadController : ControllerBase
{
    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate([FromBody] InitiateMinioRequest request, CancellationToken ct)
    {
        var username = User.Identity?.Name ?? "unknown";
        var result = await _initiateHandler.HandleAsync(/* ... */, ct);
        return Ok(result);
    }

    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteMinioRequest request, CancellationToken ct)
    {
        await _completeHandler.HandleAsync(/* ... */, ct);
        return Ok();
    }

    [HttpDelete("abort")]
    public async Task<IActionResult> Abort([FromQuery] Guid uploadId, CancellationToken ct)
    {
        await _cancelHandler.HandleAsync(new CancelUploadCommand(uploadId), ct);
        return NoContent();
    }
}
```

### CORS no MinIO (minio-setup no Docker Compose)

A configuração CORS deve ser adicionada ao script `minio-setup`:
```bash
mc alias set local http://minio:9000 minioadmin minioadmin123
mc mb --ignore-existing local/uploads
# Configurar CORS é feito via API no startup do backend (PutBucketCorsAsync não existe no mc CLI)
```

**Alternativa:** Configurar CORS via `PutCORSConfigurationAsync` no startup do backend.

### Lifecycle Rule (startup do backend)

```csharp
await _s3Client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
{
    BucketName = _bucketName,
    Configuration = new LifecycleConfiguration
    {
        Rules = new List<LifecycleRule>
        {
            new LifecycleRule
            {
                Id = "abort-incomplete-multipart",
                Status = LifecycleRuleStatus.Enabled,
                AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload
                {
                    DaysAfterInitiation = 3
                }
            }
        }
    }
});
```

## Critérios de Sucesso

- `POST /api/v1/uploads/minio/initiate` retorna `uploadId` e lista de pre-signed URLs
- Pre-signed URLs são válidas por 24h
- `PUT` manual em uma pre-signed URL envia dados ao MinIO com sucesso
- `POST /api/v1/uploads/minio/complete` consolida o multipart no MinIO
- Evento `upload.completed` é publicado no RabbitMQ após `complete`
- `DELETE /api/v1/uploads/minio/abort` cancela o multipart e atualiza status para `Cancelled`
- Lifecycle rule expira multipart incompletos após 3 dias (verificar via MinIO Console)
- Retry com Polly funciona para operações no MinIO
- Backend não recebe bytes do arquivo (CPU/RAM estáveis)
