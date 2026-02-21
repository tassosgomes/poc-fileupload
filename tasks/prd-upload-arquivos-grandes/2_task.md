---
status: pending
parallelizable: false
blocked_by: ["1.0"]
---

<task_context>
<domain>engine/domain</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>low</complexity>
<dependencies>none</dependencies>
<unblocks>"3.0"</unblocks>
</task_context>

# Tarefa 2.0: Camada Domain — Entidades, Enums e Interfaces

## Visão Geral

Implementar a camada de domínio puro da aplicação: entidade `FileUpload` com sua máquina de estados, enum `UploadStatus`, evento `UploadCompletedEvent` e todas as interfaces de repositório e serviços. Esta camada não possui nenhuma dependência externa (NuGet) — é 100% C# puro.

O domínio é compartilhado entre os dois cenários de upload (TUS e MinIO), sendo a fundação para todas as camadas superiores.

## Requisitos

- A entidade `FileUpload` deve encapsular toda a lógica de transição de estados (Pending → Completed, Pending → Corrupted, Pending → Cancelled, Pending → Failed).
- As transições inválidas devem lançar exceções (ex: não é possível marcar como `Completed` um upload `Cancelled`).
- As interfaces devem seguir o padrão de inversão de dependência (Domain define contratos, Infra implementa).
- Código em inglês, PascalCase, conforme padrões do projeto.

## Subtarefas

- [ ] 2.1 Criar enum `UploadStatus` em `3-Domain/UploadPoc.Domain/Enums/UploadStatus.cs`:
  - `Pending`, `Completed`, `Corrupted`, `Cancelled`, `Failed`
- [ ] 2.2 Criar entidade `FileUpload` em `3-Domain/UploadPoc.Domain/Entities/FileUpload.cs`:
  - Propriedades: `Id`, `FileName`, `FileSizeBytes`, `ContentType`, `ExpectedSha256`, `ActualSha256`, `UploadScenario`, `StorageKey`, `MinioUploadId`, `Status`, `CreatedBy`, `CreatedAt`, `CompletedAt`
  - Construtor que valida campos obrigatórios e inicializa status como `Pending`
  - Método `MarkCompleted(string actualSha256, string storageKey)` — valida que status é `Pending`
  - Método `MarkCorrupted(string actualSha256)` — valida que status é `Pending`
  - Método `MarkCancelled()` — valida que status é `Pending`
  - Método `MarkFailed(string reason)` — valida que status é `Pending`
  - Método `SetStorageKey(string storageKey)` — para associar o storage key após registro
  - Método `SetMinioUploadId(string minioUploadId)` — para cenário MinIO
- [ ] 2.3 Criar evento `UploadCompletedEvent` em `3-Domain/UploadPoc.Domain/Events/UploadCompletedEvent.cs`:
  - Propriedades: `UploadId` (Guid), `StorageKey`, `ExpectedSha256`, `UploadScenario`, `Timestamp`
- [ ] 2.4 Criar interface `IFileUploadRepository` em `3-Domain/UploadPoc.Domain/Interfaces/IFileUploadRepository.cs`:
  - `AddAsync`, `GetByIdAsync`, `GetAllAsync`, `GetPendingOlderThanAsync`, `UpdateAsync`
- [ ] 2.5 Criar interface `IStorageService` em `3-Domain/UploadPoc.Domain/Interfaces/IStorageService.cs`:
  - `ComputeSha256Async`, `DeleteAsync`, `ExistsAsync`
- [ ] 2.6 Criar interface `IEventPublisher` em `3-Domain/UploadPoc.Domain/Interfaces/IEventPublisher.cs`:
  - `PublishUploadCompletedAsync`, `PublishUploadTimeoutAsync`
- [ ] 2.7 Criar interface `IChecksumService` em `3-Domain/UploadPoc.Domain/Interfaces/IChecksumService.cs`:
  - `ComputeSha256Async(Stream stream, CancellationToken ct)`
- [ ] 2.8 Validar que o projeto `UploadPoc.Domain` compila sem dependências externas

## Sequenciamento

- Bloqueado por: 1.0 (Scaffolding do Projeto)
- Desbloqueia: 3.0 (PostgreSQL + EF Core)
- Paralelizável: Não (é pré-requisito direto de 3.0)

## Detalhes de Implementação

### Entidade FileUpload

```csharp
public class FileUpload
{
    public Guid Id { get; private set; }
    public string FileName { get; private set; }
    public long FileSizeBytes { get; private set; }
    public string ContentType { get; private set; }
    public string ExpectedSha256 { get; private set; }
    public string? ActualSha256 { get; private set; }
    public string UploadScenario { get; private set; } // "TUS" | "MINIO"
    public string? StorageKey { get; private set; }
    public string? MinioUploadId { get; private set; }
    public UploadStatus Status { get; private set; }
    public string CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    // Construtor para criação
    public FileUpload(string fileName, long fileSizeBytes, string contentType,
                      string expectedSha256, string uploadScenario, string createdBy)
    {
        // Validações de campos obrigatórios
        Id = Guid.NewGuid();
        Status = UploadStatus.Pending;
        CreatedAt = DateTime.UtcNow;
        // ... atribuições
    }

    // Construtor privado para EF Core
    private FileUpload() { }

    public void MarkCompleted(string actualSha256, string storageKey)
    {
        if (Status != UploadStatus.Pending)
            throw new InvalidOperationException($"Cannot complete upload with status {Status}");
        ActualSha256 = actualSha256;
        StorageKey = storageKey;
        Status = UploadStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkCorrupted(string actualSha256)
    {
        if (Status != UploadStatus.Pending)
            throw new InvalidOperationException($"Cannot mark as corrupted upload with status {Status}");
        ActualSha256 = actualSha256;
        Status = UploadStatus.Corrupted;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkCancelled()
    {
        if (Status != UploadStatus.Pending)
            throw new InvalidOperationException($"Cannot cancel upload with status {Status}");
        Status = UploadStatus.Cancelled;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        if (Status != UploadStatus.Pending)
            throw new InvalidOperationException($"Cannot fail upload with status {Status}");
        Status = UploadStatus.Failed;
        CompletedAt = DateTime.UtcNow;
    }
}
```

### Enum UploadStatus

```csharp
public enum UploadStatus
{
    Pending,    // PENDENTE — upload em andamento
    Completed,  // CONCLUÍDO — integridade validada
    Corrupted,  // CORROMPIDO — SHA-256 divergente
    Cancelled,  // CANCELADO — cancelado pelo usuário
    Failed      // FALHA — timeout ou erro irrecuperável
}
```

### Evento UploadCompletedEvent

```csharp
public record UploadCompletedEvent(
    Guid UploadId,
    string StorageKey,
    string ExpectedSha256,
    string UploadScenario,
    DateTime Timestamp
);
```

### Interfaces

Seguir exatamente as assinaturas definidas na techspec (seção "Interfaces Principais"). Todas recebem `CancellationToken` como último parâmetro.

## Critérios de Sucesso

- Projeto `UploadPoc.Domain` compila sem nenhuma dependência NuGet externa
- Entidade `FileUpload` possui construtores (público e privado para EF Core)
- Transições de status válidas: `Pending → Completed`, `Pending → Corrupted`, `Pending → Cancelled`, `Pending → Failed`
- Transições inválidas lançam `InvalidOperationException` (ex: `Completed → Cancelled`)
- Todas as 4 interfaces (`IFileUploadRepository`, `IStorageService`, `IEventPublisher`, `IChecksumService`) estão definidas
- Evento `UploadCompletedEvent` é um `record` imutável
