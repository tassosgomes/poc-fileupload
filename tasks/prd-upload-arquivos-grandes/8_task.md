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
<dependencies>http_server</dependencies>
<unblocks>"9.0"</unblocks>
</task_context>

# Tarefa 8.0: Upload TUS — Backend (F03)

## Visão Geral

Implementar o cenário de upload via protocolo TUS no backend usando `tusdotnet`. O middleware TUS gerencia o recebimento de chunks (100 MB sequenciais), grava em disco (PVC) e dispara o evento `upload.completed` no RabbitMQ via callback `OnFileCompleteAsync`. A autenticação JWT é validada manualmente no callback de configuração do TUS (o middleware intercepta antes do pipeline de auth do ASP.NET Core).

## Requisitos

- RF03.1: Chunks de 100 MB enviados sequencialmente via protocolo TUS.
- RF03.3: Suporte a pause/resume (nativo do TUS).
- RF03.5: Cancelamento remove arquivo parcial do disco.
- RF03.6: Suporte a arquivos até 250 GB (configurável até 300 GB).
- RF03.7: Cancelamento atualiza status e remove arquivo parcial.

## Subtarefas

- [ ] 8.1 Criar `TusDiskStorageService` em `4-Infra/UploadPoc.Infra/Storage/TusDiskStorageService.cs`:
  - Implementar `IStorageService` (para cenário TUS)
  - `ComputeSha256Async(storageKey)` → ler arquivo do disco e calcular SHA-256
  - `DeleteAsync(storageKey)` → remover arquivo do disco (path = TusStorage:Path + storageKey)
  - `ExistsAsync(storageKey)` → verificar se arquivo existe no disco
- [ ] 8.2 Configurar middleware `tusdotnet` no `Program.cs`:
  - Mapear em `/upload/tus` via `app.MapTus()`
  - Store: `TusDiskStore` apontando para `TusStorage:Path` (default: `/app/uploads`)
  - `MaxAllowedUploadSizeInBytes = 300L * 1024 * 1024 * 1024` (300 GB)
  - `OnAuthorizeAsync`: validar JWT manualmente usando `JwtService.ValidateToken()`
  - `OnCreateCompleteAsync`: armazenar `uploadId` nos metadados TUS para correlação com PostgreSQL
  - `OnFileCompleteAsync`: publicar `UploadCompletedEvent` no RabbitMQ
- [ ] 8.3 Implementar validação manual de JWT no callback `OnAuthorizeAsync`:
  - Extrair token do header `Authorization: Bearer <token>`
  - Usar `JwtService.ValidateToken()` (criado na tarefa 5.0)
  - Se inválido, definir `eventContext.FailRequest(HttpStatusCode.Unauthorized)`
- [ ] 8.4 Implementar callback `OnCreateCompleteAsync`:
  - Extrair `uploadId` dos metadados TUS (`metadata.uploadId`)
  - Buscar o registro no PostgreSQL pelo `uploadId`
  - Associar o `fileId` do TUS como `StorageKey` na entidade
  - Persistir atualização
- [ ] 8.5 Implementar callback `OnFileCompleteAsync`:
  - Buscar registro pelo `StorageKey` (fileId do TUS)
  - Publicar `UploadCompletedEvent` no RabbitMQ com `storageKey`, `expectedSha256` e `uploadScenario = "TUS"`
  - Logar conclusão: `{ uploadId, fileName, durationMs }`
- [ ] 8.6 Atualizar `TusUploadController` para documentar o fluxo:
  - `POST /api/v1/uploads/tus/register` (já implementado em 6.0) → retorna `uploadId` e instrução para usar `/upload/tus`
  - O endpoint TUS (`/upload/tus`) é gerenciado pelo middleware `tusdotnet`, não pelo controller
- [ ] 8.7 Registrar `TusDiskStorageService` no DI
- [ ] 8.8 Testar upload via `tus-js-client` ou curl com protocolo TUS:
  - POST /upload/tus (creation)
  - PATCH /upload/tus/{fileId} (chunks)
  - HEAD /upload/tus/{fileId} (verificar offset para resume)

## Sequenciamento

- Bloqueado por: 6.0 (Registro de Metadados — precisa do registro no PostgreSQL para correlacionar com o TUS fileId)
- Desbloqueia: 9.0 (Consumer de Integridade — precisa do `OnFileCompleteAsync` publicando eventos)
- Paralelizável: Não (depende de 6.0, embora a implementação em si não dependa de 7.0)

## Detalhes de Implementação

### Configuração do tusdotnet no Program.cs

```csharp
app.MapTus("/upload/tus", async httpContext => new()
{
    Store = new TusDiskStore(builder.Configuration["TusStorage:Path"] ?? "/app/uploads"),
    MaxAllowedUploadSizeInBytes = 300L * 1024 * 1024 * 1024, // 300 GB

    Events = new()
    {
        OnAuthorizeAsync = async eventContext =>
        {
            // Validação manual de JWT — tusdotnet intercepta antes do pipeline auth
            var authHeader = eventContext.HttpContext.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                eventContext.FailRequest(HttpStatusCode.Unauthorized, "Missing or invalid Authorization header");
                return;
            }

            var token = authHeader.Substring("Bearer ".Length);
            var jwtService = eventContext.HttpContext.RequestServices.GetRequiredService<JwtService>();
            var principal = jwtService.ValidateToken(token);
            if (principal == null)
            {
                eventContext.FailRequest(HttpStatusCode.Unauthorized, "Invalid JWT token");
                return;
            }

            // Armazenar principal no HttpContext para uso posterior
            eventContext.HttpContext.User = principal;
        },

        OnCreateCompleteAsync = async eventContext =>
        {
            // Correlacionar fileId do TUS com uploadId do PostgreSQL
            var fileId = eventContext.FileId;
            var metadata = await eventContext.GetMetadataAsync(eventContext.CancellationToken);

            if (metadata.TryGetValue("uploadId", out var uploadIdMeta))
            {
                var uploadId = Guid.Parse(uploadIdMeta.GetString(Encoding.UTF8));
                var repo = eventContext.HttpContext.RequestServices
                    .GetRequiredService<IFileUploadRepository>();
                var upload = await repo.GetByIdAsync(uploadId, eventContext.CancellationToken);
                if (upload != null)
                {
                    upload.SetStorageKey(fileId);
                    await repo.UpdateAsync(upload, eventContext.CancellationToken);
                }
            }
        },

        OnFileCompleteAsync = async eventContext =>
        {
            // Publicar evento no RabbitMQ
            var fileId = eventContext.FileId;
            var repo = eventContext.HttpContext.RequestServices
                .GetRequiredService<IFileUploadRepository>();
            var publisher = eventContext.HttpContext.RequestServices
                .GetRequiredService<IEventPublisher>();

            // Buscar upload pelo StorageKey (fileId do TUS)
            // Nota: pode precisar de um método adicional no repo para buscar por StorageKey
            // Alternativa: usar metadados TUS para obter o uploadId

            var metadata = await eventContext.GetMetadataAsync(eventContext.CancellationToken);
            if (metadata.TryGetValue("uploadId", out var uploadIdMeta))
            {
                var uploadId = Guid.Parse(uploadIdMeta.GetString(Encoding.UTF8));
                var upload = await repo.GetByIdAsync(uploadId, eventContext.CancellationToken);
                if (upload != null)
                {
                    await publisher.PublishUploadCompletedAsync(
                        new UploadCompletedEvent(
                            upload.Id,
                            fileId,
                            upload.ExpectedSha256,
                            "TUS",
                            DateTime.UtcNow),
                        eventContext.CancellationToken);
                }
            }
        }
    }
});
```

### TusDiskStorageService

```csharp
public class TusDiskStorageService : IStorageService
{
    private readonly string _basePath;
    private readonly IChecksumService _checksumService;

    public TusDiskStorageService(IConfiguration config, IChecksumService checksumService)
    {
        _basePath = config["TusStorage:Path"] ?? "/app/uploads";
        _checksumService = checksumService;
    }

    public async Task<string> ComputeSha256Async(string storageKey, CancellationToken ct)
    {
        var filePath = Path.Combine(_basePath, storageKey);
        await using var stream = File.OpenRead(filePath);
        return await _checksumService.ComputeSha256Async(stream, ct);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        var filePath = Path.Combine(_basePath, storageKey);
        if (File.Exists(filePath))
            File.Delete(filePath);

        // TUS também cria arquivo de metadados (.metadata, .uploadlength, etc.)
        var metaFiles = Directory.GetFiles(_basePath, $"{storageKey}.*");
        foreach (var meta in metaFiles)
            File.Delete(meta);

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken ct)
    {
        var filePath = Path.Combine(_basePath, storageKey);
        return Task.FromResult(File.Exists(filePath));
    }
}
```

### Metadados TUS no Frontend

O `tus-js-client` deve enviar o `uploadId` nos metadados:
```typescript
const upload = new tus.Upload(file, {
  endpoint: "/upload/tus",
  metadata: {
    uploadId: registeredUpload.id,
    filename: file.name,
    filetype: file.type,
  },
  chunkSize: 100 * 1024 * 1024, // 100 MB
  headers: { Authorization: `Bearer ${token}` },
});
```

## Critérios de Sucesso

- `POST /upload/tus` (TUS creation) com JWT válido cria arquivo no disco
- `PATCH /upload/tus/{fileId}` envia chunks de 100 MB sequencialmente
- `HEAD /upload/tus/{fileId}` retorna offset correto para resume
- Requisição sem JWT retorna 401
- `OnFileCompleteAsync` publica `UploadCompletedEvent` no RabbitMQ
- `StorageKey` é atualizado no PostgreSQL com o `fileId` do TUS
- Cancelamento remove arquivo parcial do disco e metadados do TUS
- Upload de arquivo > 1 GB funciona sem erro
- TUS resume funciona: upload parcial retorna offset correto no HEAD
