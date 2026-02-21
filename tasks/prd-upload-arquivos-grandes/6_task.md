---
status: pending
parallelizable: false
blocked_by: ["4.0", "5.0"]
---

<task_context>
<domain>engine/application</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>medium</complexity>
<dependencies>database</dependencies>
<unblocks>"7.0", "8.0"</unblocks>
</task_context>

# Tarefa 6.0: Registro de Metadados e Cancelamento (F02 — Base)

## Visão Geral

Implementar a base compartilhada entre os dois cenários de upload: registro de metadados no PostgreSQL (status PENDENTE) e cancelamento de upload. Inclui commands, handlers, DTOs e validators (FluentValidation). Esta lógica é reutilizada tanto pelo cenário TUS quanto pelo MinIO.

O registro de metadados é o primeiro passo de qualquer upload — antes de qualquer byte ser enviado, o backend já tem um registro no PostgreSQL para rastreabilidade.

## Requisitos

- RF02.1: Registrar no PostgreSQL: nome, tamanho, tipo MIME, SHA-256 esperado, usuário, data, status PENDENTE.
- O endpoint de registro para TUS é separado do endpoint de initiate do MinIO (mas compartilham a mesma lógica base).
- Validação com FluentValidation: nome obrigatório, tamanho > 0, SHA-256 com 64 caracteres hex.
- Cancelamento atualiza status para CANCELADO.

## Subtarefas

- [ ] 6.1 Criar `RegisterUploadCommand` em `2-Application/UploadPoc.Application/Commands/RegisterUploadCommand.cs`:
  - Propriedades: `FileName`, `FileSizeBytes`, `ContentType`, `ExpectedSha256`, `UploadScenario`, `CreatedBy`
- [ ] 6.2 Criar `RegisterUploadHandler` em `2-Application/UploadPoc.Application/Handlers/RegisterUploadHandler.cs`:
  - Recebe `RegisterUploadCommand`
  - Cria entidade `FileUpload` com status `Pending`
  - Persiste via `IFileUploadRepository.AddAsync`
  - Retorna `UploadDto` com `Id` e `StorageKey`
- [ ] 6.3 Criar `CancelUploadCommand` em `2-Application/UploadPoc.Application/Commands/CancelUploadCommand.cs`:
  - Propriedades: `UploadId` (Guid)
- [ ] 6.4 Criar `CancelUploadHandler` em `2-Application/UploadPoc.Application/Handlers/CancelUploadHandler.cs`:
  - Busca upload por ID
  - Valida que existe e está `Pending`
  - Chama `MarkCancelled()` na entidade
  - Remove arquivo do storage via `IStorageService.DeleteAsync` (se `StorageKey` não null)
  - Para MinIO: chama `AbortMultipartUpload` (via `IStorageService` ou serviço dedicado)
  - Persiste atualização
- [ ] 6.5 Criar `UploadDto` em `2-Application/UploadPoc.Application/Dtos/UploadDto.cs`:
  - Propriedades: `Id`, `FileName`, `FileSizeBytes`, `ContentType`, `ExpectedSha256`, `ActualSha256`, `UploadScenario`, `StorageKey`, `Status`, `CreatedBy`, `CreatedAt`, `CompletedAt`
- [ ] 6.6 Criar `RegisterUploadValidator` em `2-Application/UploadPoc.Application/Validators/RegisterUploadValidator.cs`:
  - `FileName`: NotEmpty, MaxLength(500)
  - `FileSizeBytes`: GreaterThan(0)
  - `ContentType`: NotEmpty, MaxLength(100)
  - `ExpectedSha256`: NotEmpty, Length(64), Matches(`^[a-fA-F0-9]{64}$`)
  - `UploadScenario`: Must be "TUS" or "MINIO"
- [ ] 6.7 Criar `TusUploadController` (parcial) em `1-Services/UploadPoc.API/Controllers/TusUploadController.cs`:
  - `POST /api/v1/uploads/tus/register` — chama `RegisterUploadHandler` com scenario "TUS"
  - `DELETE /api/v1/uploads/{id}/cancel` — chama `CancelUploadHandler`
- [ ] 6.8 Registrar handlers e validators no DI container
- [ ] 6.9 Testar registro + cancelamento via Swagger

## Sequenciamento

- Bloqueado por: 4.0 (RabbitMQ — para o handler de cancelamento publicar eventos se necessário), 5.0 (JWT — para proteger endpoints)
- Desbloqueia: 7.0 (Upload MinIO), 8.0 (Upload TUS)
- Paralelizável: Não (é pré-requisito de 7.0 e 8.0)

## Detalhes de Implementação

### RegisterUploadCommand

```csharp
public record RegisterUploadCommand(
    string FileName,
    long FileSizeBytes,
    string ContentType,
    string ExpectedSha256,
    string UploadScenario,
    string CreatedBy
);
```

### RegisterUploadHandler

```csharp
public class RegisterUploadHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly IValidator<RegisterUploadCommand> _validator;
    private readonly ILogger<RegisterUploadHandler> _logger;

    public async Task<UploadDto> HandleAsync(RegisterUploadCommand command, CancellationToken ct)
    {
        // 1. Validar command
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        // 2. Criar entidade
        var upload = new FileUpload(
            command.FileName,
            command.FileSizeBytes,
            command.ContentType,
            command.ExpectedSha256,
            command.UploadScenario,
            command.CreatedBy
        );

        // 3. Persistir
        await _repository.AddAsync(upload, ct);

        _logger.LogInformation("Upload registrado: {UploadId} {FileName} {Scenario} {FileSize}",
            upload.Id, upload.FileName, upload.UploadScenario, upload.FileSizeBytes);

        // 4. Retornar DTO
        return MapToDto(upload);
    }
}
```

### CancelUploadHandler

```csharp
public class CancelUploadHandler
{
    private readonly IFileUploadRepository _repository;
    private readonly IStorageService _tusStorage;
    private readonly IStorageService _minioStorage;
    private readonly ILogger<CancelUploadHandler> _logger;

    public async Task HandleAsync(CancelUploadCommand command, CancellationToken ct)
    {
        var upload = await _repository.GetByIdAsync(command.UploadId, ct)
            ?? throw new KeyNotFoundException($"Upload {command.UploadId} not found");

        upload.MarkCancelled();

        // Limpar storage
        if (!string.IsNullOrEmpty(upload.StorageKey))
        {
            var storage = upload.UploadScenario == "TUS" ? _tusStorage : _minioStorage;
            await storage.DeleteAsync(upload.StorageKey, ct);
        }

        await _repository.UpdateAsync(upload, ct);

        _logger.LogInformation("Upload cancelado: {UploadId}", upload.Id);
    }
}
```

### RegisterUploadValidator

```csharp
public class RegisterUploadValidator : AbstractValidator<RegisterUploadCommand>
{
    public RegisterUploadValidator()
    {
        RuleFor(x => x.FileName)
            .NotEmpty().WithMessage("File name is required")
            .MaximumLength(500);

        RuleFor(x => x.FileSizeBytes)
            .GreaterThan(0).WithMessage("File size must be greater than 0");

        RuleFor(x => x.ContentType)
            .NotEmpty().WithMessage("Content type is required")
            .MaximumLength(100);

        RuleFor(x => x.ExpectedSha256)
            .NotEmpty().WithMessage("SHA-256 hash is required")
            .Length(64).WithMessage("SHA-256 hash must be 64 characters")
            .Matches("^[a-fA-F0-9]{64}$").WithMessage("SHA-256 hash must be hexadecimal");

        RuleFor(x => x.UploadScenario)
            .Must(s => s == "TUS" || s == "MINIO")
            .WithMessage("Upload scenario must be TUS or MINIO");
    }
}
```

### TusUploadController (parcial)

```csharp
[ApiController]
[Route("api/v1/uploads")]
[Authorize]
public class TusUploadController : ControllerBase
{
    [HttpPost("tus/register")]
    public async Task<IActionResult> Register([FromBody] RegisterUploadRequest request, CancellationToken ct)
    {
        var username = User.Identity?.Name ?? "unknown";
        var command = new RegisterUploadCommand(
            request.FileName, request.FileSizeBytes, request.ContentType,
            request.ExpectedSha256, "TUS", username);
        var result = await _handler.HandleAsync(command, ct);
        return Ok(result);
    }

    [HttpDelete("{id}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await _cancelHandler.HandleAsync(new CancelUploadCommand(id), ct);
        return NoContent();
    }
}
```

## Critérios de Sucesso

- `POST /api/v1/uploads/tus/register` com dados válidos cria registro no PostgreSQL com status `Pending`
- Validação rejeita SHA-256 com menos de 64 chars (400 Bad Request)
- Validação rejeita `FileSizeBytes <= 0`
- `DELETE /api/v1/uploads/{id}/cancel` atualiza status para `Cancelled`
- Cancelamento de upload inexistente retorna 404
- Cancelamento de upload já concluído retorna erro (InvalidOperationException → 400)
- Logs informam registro e cancelamento com `UploadId`
