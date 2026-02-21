---
status: pending
parallelizable: true
blocked_by: ["9.0"]
---

<task_context>
<domain>engine/api</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>medium</complexity>
<dependencies>http_server</dependencies>
<unblocks>"15.0"</unblocks>
</task_context>

# Tarefa 10.0: Listagem e Download de Arquivos (F05)

## Visão Geral

Implementar os endpoints de listagem de arquivos e download no backend. A listagem consulta o PostgreSQL e exibe todos os uploads com status, tamanho, data e checksum. O download é diferenciado por cenário: TUS serve o arquivo diretamente do disco com suporte a Range Requests; MinIO faz redirect para pre-signed URL (1h de validade).

## Requisitos

- RF05.1: Listar todos os arquivos com status, nome, tamanho, data e checksum.
- RF05.2: Mostrar status de cada arquivo (PENDENTE, CONCLUÍDO, CORROMPIDO, CANCELADO).
- RF05.3: Apenas CONCLUÍDO tem download habilitado.
- RF05.4: Download TUS via backend com `enableRangeProcessing`.
- RF05.5: Download MinIO via redirect para pre-signed URL.
- RF05.6: Download preserva nome original.

## Subtarefas

- [ ] 10.1 Criar `ListUploadsQuery` em `2-Application/UploadPoc.Application/Queries/ListUploadsQuery.cs`
- [ ] 10.2 Criar `ListUploadsHandler` em `2-Application/UploadPoc.Application/Handlers/ListUploadsHandler.cs`:
  - Usa `IFileUploadRepository.GetAllAsync()` — retorna lista ordenada por `CreatedAt` desc
  - Mapeia para lista de `UploadDto`
- [ ] 10.3 Criar `GetDownloadUrlQuery` em `2-Application/UploadPoc.Application/Queries/GetDownloadUrlQuery.cs`
- [ ] 10.4 Criar `GetDownloadUrlHandler` em `2-Application/UploadPoc.Application/Handlers/GetDownloadUrlHandler.cs`:
  - Busca upload por ID
  - Valida que status é `Completed` (caso contrário, retorna erro)
  - Para TUS: retorna path do arquivo no disco
  - Para MinIO: gera pre-signed URL GET com validade de 1h
- [ ] 10.5 Criar `FilesController` em `1-Services/UploadPoc.API/Controllers/FilesController.cs`:
  - `GET /api/v1/files` → lista todos os uploads (UploadDto[])
  - `GET /api/v1/files/{id}/download`:
    - TUS: `PhysicalFile(filePath, contentType, fileName, enableRangeProcessing: true)`
    - MinIO: `Redirect(presignedUrl)` (HTTP 302)
- [ ] 10.6 Adicionar método `GeneratePresignedDownloadUrl` no `MinioStorageService`:
  - `GetPreSignedURL` com verb GET e expiração de 1h
- [ ] 10.7 Registrar handlers no DI
- [ ] 10.8 Testar listagem e download via Swagger/Postman

## Sequenciamento

- Bloqueado por: 9.0 (Consumer — precisa que uploads tenham status `Completed` para testar download)
- Desbloqueia: 15.0 (Frontend — Listagem e Download)
- Paralelizável: Sim (pode ser feito em paralelo com 11.0 — Detecção de Órfãos)

## Detalhes de Implementação

### FilesController

```csharp
[ApiController]
[Route("api/v1/files")]
[Authorize]
public class FilesController : ControllerBase
{
    private readonly ListUploadsHandler _listHandler;
    private readonly GetDownloadUrlHandler _downloadHandler;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var files = await _listHandler.HandleAsync(new ListUploadsQuery(), ct);
        return Ok(files);
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var result = await _downloadHandler.HandleAsync(new GetDownloadUrlQuery(id), ct);

        if (result.Scenario == "TUS")
        {
            return PhysicalFile(
                result.FilePath!,
                result.ContentType,
                result.FileName,
                enableRangeProcessing: true);
        }
        else // MINIO
        {
            return Redirect(result.PresignedUrl!);
        }
    }
}
```

### GetDownloadUrlHandler

```csharp
public class GetDownloadUrlHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly MinioStorageService _minioStorage;
    private readonly IConfiguration _config;

    public async Task<DownloadResult> HandleAsync(GetDownloadUrlQuery query, CancellationToken ct)
    {
        var upload = await _repository.GetByIdAsync(query.UploadId, ct)
            ?? throw new KeyNotFoundException($"Upload {query.UploadId} not found");

        if (upload.Status != UploadStatus.Completed)
            throw new InvalidOperationException($"Download not available for upload with status {upload.Status}");

        if (upload.UploadScenario == "TUS")
        {
            var basePath = _config["TusStorage:Path"] ?? "/app/uploads";
            var filePath = Path.Combine(basePath, upload.StorageKey!);
            return new DownloadResult("TUS", upload.FileName, upload.ContentType, filePath, null);
        }
        else
        {
            var presignedUrl = _minioStorage.GeneratePresignedDownloadUrl(upload.StorageKey!);
            return new DownloadResult("MINIO", upload.FileName, upload.ContentType, null, presignedUrl);
        }
    }
}

public record DownloadResult(
    string Scenario,
    string FileName,
    string ContentType,
    string? FilePath,
    string? PresignedUrl
);
```

### Pre-signed Download URL (MinIO)

```csharp
public string GeneratePresignedDownloadUrl(string storageKey)
{
    return _s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = _bucketName,
        Key = storageKey,
        Verb = HttpVerb.GET,
        Expires = DateTime.UtcNow.AddHours(1),
        ResponseHeaderOverrides = new ResponseHeaderOverrides
        {
            ContentDisposition = $"attachment; filename=\"{Path.GetFileName(storageKey)}\""
        }
    });
}
```

### UploadDto (já criado em 6.0, confirmar que tem todos os campos)

```csharp
public record UploadDto(
    Guid Id,
    string FileName,
    long FileSizeBytes,
    string ContentType,
    string ExpectedSha256,
    string? ActualSha256,
    string UploadScenario,
    string? StorageKey,
    string Status,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime? CompletedAt
);
```

## Critérios de Sucesso

- `GET /api/v1/files` retorna lista de todos os uploads com todos os campos
- Lista ordenada por `CreatedAt` descendente (mais recentes primeiro)
- `GET /api/v1/files/{id}/download` para upload TUS `Completed` retorna arquivo com headers corretos
- Download TUS suporta Range Requests (HTTP 206 Partial Content)
- `GET /api/v1/files/{id}/download` para upload MinIO `Completed` retorna redirect 302 para pre-signed URL
- Download de upload `Pending` retorna erro (400/403)
- Download de upload inexistente retorna 404
- Pre-signed URL de download tem validade de 1 hora
- Nome original do arquivo é preservado no download (Content-Disposition)
