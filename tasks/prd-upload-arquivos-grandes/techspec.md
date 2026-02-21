# Especificação Técnica — POC Upload de Arquivos Grandes (TUS + MinIO)

## Resumo Executivo

Esta especificação técnica detalha a implementação de uma POC que compara duas estratégias de upload de arquivos grandes (até 250 GB): **protocolo TUS** (upload resumable via backend com `tusdotnet`) e **MinIO Multipart Upload** (upload direto via pre-signed URLs com `AWSSDK.S3`). A arquitetura segue **Clean Architecture** com 5 camadas, backend em **.NET 8**, frontend em **React + Vite + TypeScript** (feature-based), persistência em **PostgreSQL via EF Core**, mensageria com **RabbitMQ.Client** (publisher confirms + manual ack + DLQ) e orquestração via **Docker Compose**. Para Kubernetes, a POC produz apenas os YAMLs de Deployment/Service/Ingress do backend e frontend — PostgreSQL, RabbitMQ e MinIO já existem no cluster.

A decisão arquitetural central é manter **dois cenários de upload coexistindo na mesma aplicação**, compartilhando a camada de domínio (entidade `FileUpload`, status machine, eventos) e diferenciando apenas na camada de infraestrutura (storage TUS em disco vs. storage MinIO via S3). O ciclo de vida é unificado: `PENDENTE → CONCLUÍDO | CORROMPIDO | CANCELADO | FALHA`, com garantia de zero dados órfãos via job periódico + dead-letter queue.

---

## Arquitetura do Sistema

### Visão Geral dos Componentes

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        Frontend (React + Vite)                          │
│  features/auth · features/upload-tus · features/upload-minio · files    │
└───────────────┬──────────────────────────────────┬──────────────────────┘
                │ HTTP/TUS                         │ PUT direto (pre-signed)
                ▼                                  ▼
┌──────────────────────────┐              ┌─────────────────────┐
│  Backend .NET 8 (API)    │              │       MinIO         │
│  ┌─────────────────────┐ │              │  (Object Storage)   │
│  │ 1-Services (API)    │ │              └─────────────────────┘
│  │ 2-Application       │ │                       ▲
│  │ 3-Domain            │ │   SDK S3 (orquestra)  │
│  │ 4-Infra             │ ├───────────────────────┘
│  └─────────────────────┘ │
└──────────┬───────────────┘
           │
     ┌─────┴─────────────────────┐
     │                           │
     ▼                           ▼
┌──────────┐            ┌──────────────┐
│PostgreSQL│            │  RabbitMQ    │
│(metadata)│            │(eventos DLQ) │
└──────────┘            └──────────────┘
```

**Componentes e responsabilidades:**

| Componente | Responsabilidade |
|---|---|
| **Frontend React** | UI de login, upload (TUS e MinIO), listagem de arquivos, download. Calcula SHA-256 via Web Worker. |
| **Backend .NET 8** | Autenticação JWT, endpoints REST, orquestração TUS/MinIO, publicação de eventos, job de limpeza. |
| **PostgreSQL** | Registro de metadados e status de cada upload (`FileUpload`). Fonte da verdade. |
| **RabbitMQ** | Canal de eventos assíncronos (`upload.completed`, `upload.timeout`). DLQ para mensagens falhadas. |
| **MinIO** | Object storage S3-compatible. Recebe bytes diretamente do frontend no cenário MinIO. |
| **Disco (PVC)** | Storage local para chunks TUS. Volume local no Docker Compose; PVC ReadWriteMany já provisionado no K8s. |

### Fluxo de Dados

**Cenário TUS:**
1. Frontend → `POST /api/v1/auth/login` → JWT
2. Frontend → `POST /api/v1/uploads/tus/register` (metadados + SHA-256) → Backend cria registro `PENDENTE` no PostgreSQL → retorna `uploadId`
3. Frontend → `POST /upload/tus` (TUS protocol) → `PATCH /upload/tus/{fileId}` (chunks 100 MB) → Backend grava em PVC
4. `tusdotnet` `OnFileCompleteAsync` → Backend publica `upload.completed` no RabbitMQ
5. Consumer processa evento → calcula SHA-256 do arquivo no disco → compara com esperado → atualiza status para `CONCLUÍDO` ou `CORROMPIDO`

**Cenário MinIO:**
1. Frontend → `POST /api/v1/auth/login` → JWT
2. Frontend → `POST /api/v1/uploads/minio/initiate` (metadados + SHA-256) → Backend cria registro `PENDENTE` + inicia multipart no MinIO → retorna `uploadId` + pre-signed URLs
3. Frontend → `PUT` chunks diretamente ao MinIO (5 em paralelo, 100 MB cada) → coleta ETags
4. Frontend → `POST /api/v1/uploads/minio/complete` (ETags) → Backend chama `CompleteMultipartUpload` → publica `upload.completed` no RabbitMQ
5. Consumer processa evento → calcula SHA-256 via `GetObjectAsync` streaming → compara → atualiza status

---

## Design de Implementação

### Estrutura de Pastas do Backend

```
backend/
├── UploadPoc.sln
├── 1-Services/
│   └── UploadPoc.API/
│       ├── UploadPoc.API.csproj
│       ├── Program.cs
│       ├── Controllers/
│       │   ├── AuthController.cs
│       │   ├── TusUploadController.cs
│       │   ├── MinioUploadController.cs
│       │   └── FilesController.cs
│       ├── Middleware/
│       │   └── ExceptionHandlingMiddleware.cs
│       └── appsettings.json
├── 2-Application/
│   └── UploadPoc.Application/
│       ├── UploadPoc.Application.csproj
│       ├── Commands/
│       │   ├── RegisterUploadCommand.cs
│       │   ├── CompleteUploadCommand.cs
│       │   ├── CancelUploadCommand.cs
│       │   └── InitiateMinioUploadCommand.cs
│       ├── Queries/
│       │   ├── ListUploadsQuery.cs
│       │   └── GetDownloadUrlQuery.cs
│       ├── Handlers/
│       │   ├── RegisterUploadHandler.cs
│       │   ├── CompleteUploadHandler.cs
│       │   ├── CancelUploadHandler.cs
│       │   ├── InitiateMinioUploadHandler.cs
│       │   ├── ListUploadsHandler.cs
│       │   └── GetDownloadUrlHandler.cs
│       ├── Consumers/
│       │   └── UploadCompletedConsumer.cs
│       ├── Jobs/
│       │   └── OrphanCleanupJob.cs
│       ├── Dtos/
│       │   ├── UploadDto.cs
│       │   ├── InitiateMinioResponse.cs
│       │   └── CompleteMinioRequest.cs
│       └── Validators/
│           ├── RegisterUploadValidator.cs
│           └── CompleteMinioValidator.cs
├── 3-Domain/
│   └── UploadPoc.Domain/
│       ├── UploadPoc.Domain.csproj
│       ├── Entities/
│       │   └── FileUpload.cs
│       ├── Enums/
│       │   └── UploadStatus.cs
│       ├── Events/
│       │   └── UploadCompletedEvent.cs
│       └── Interfaces/
│           ├── IFileUploadRepository.cs
│           ├── IStorageService.cs
│           ├── IEventPublisher.cs
│           └── IChecksumService.cs
├── 4-Infra/
│   └── UploadPoc.Infra/
│       ├── UploadPoc.Infra.csproj
│       ├── Persistence/
│       │   ├── AppDbContext.cs
│       │   ├── Configurations/
│       │   │   └── FileUploadConfiguration.cs
│       │   └── Repositories/
│       │       └── FileUploadRepository.cs
│       ├── Storage/
│       │   ├── TusDiskStorageService.cs
│       │   └── MinioStorageService.cs
│       ├── Messaging/
│       │   ├── RabbitMqPublisher.cs
│       │   └── RabbitMqConsumerHostedService.cs
│       └── Services/
│           └── Sha256ChecksumService.cs
└── 5-Tests/
    └── UploadPoc.UnitTests/
        └── UploadPoc.UnitTests.csproj
```

### Estrutura de Pastas do Frontend

```
frontend/
├── package.json
├── vite.config.ts
├── tsconfig.json
├── nginx.conf
├── Dockerfile
└── src/
    ├── main.tsx
    ├── App.tsx
    ├── features/
    │   ├── auth/
    │   │   ├── components/
    │   │   │   └── LoginPage.tsx
    │   │   ├── hooks/
    │   │   │   └── useAuth.ts
    │   │   └── services/
    │   │       └── authApi.ts
    │   ├── upload-tus/
    │   │   ├── components/
    │   │   │   └── TusUploadPage.tsx
    │   │   └── hooks/
    │   │       └── useTusUpload.ts
    │   ├── upload-minio/
    │   │   ├── components/
    │   │   │   └── MinioUploadPage.tsx
    │   │   └── hooks/
    │   │       └── useMinioUpload.ts
    │   └── files/
    │       ├── components/
    │       │   └── FileListTable.tsx
    │       └── hooks/
    │           └── useFiles.ts
    ├── components/
    │   ├── ProgressBar.tsx
    │   └── Layout.tsx
    ├── services/
    │   └── api.ts
    ├── workers/
    │   └── sha256Worker.ts
    └── types/
        └── index.ts
```

### Interfaces Principais

```csharp
// Domain — Entidade central
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

    public void MarkCompleted(string actualSha256, string storageKey) { ... }
    public void MarkCorrupted(string actualSha256) { ... }
    public void MarkCancelled() { ... }
    public void MarkFailed(string reason) { ... }
}

public enum UploadStatus
{
    Pending,    // PENDENTE — upload em andamento
    Completed,  // CONCLUÍDO — integridade validada
    Corrupted,  // CORROMPIDO — SHA-256 divergente
    Cancelled,  // CANCELADO — cancelado pelo usuário
    Failed      // FALHA — timeout ou erro irrecuperável
}
```

```csharp
// Domain — Interfaces de repositório e serviços
public interface IFileUploadRepository
{
    Task<FileUpload> AddAsync(FileUpload upload, CancellationToken ct);
    Task<FileUpload?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<FileUpload>> GetAllAsync(CancellationToken ct);
    Task<IReadOnlyList<FileUpload>> GetPendingOlderThanAsync(TimeSpan age, CancellationToken ct);
    Task UpdateAsync(FileUpload upload, CancellationToken ct);
}

public interface IStorageService
{
    Task<string> ComputeSha256Async(string storageKey, CancellationToken ct);
    Task DeleteAsync(string storageKey, CancellationToken ct);
    Task<bool> ExistsAsync(string storageKey, CancellationToken ct);
}

public interface IEventPublisher
{
    Task PublishUploadCompletedAsync(UploadCompletedEvent evt, CancellationToken ct);
    Task PublishUploadTimeoutAsync(Guid uploadId, CancellationToken ct);
}

public interface IChecksumService
{
    Task<string> ComputeSha256Async(Stream stream, CancellationToken ct);
}
```

### Modelos de Dados

**Tabela `file_uploads` (PostgreSQL):**

| Coluna | Tipo | Constraint | Descrição |
|---|---|---|---|
| `id` | `uuid` | PK | Identificador único |
| `file_name` | `varchar(500)` | NOT NULL | Nome original do arquivo |
| `file_size_bytes` | `bigint` | NOT NULL | Tamanho em bytes |
| `content_type` | `varchar(100)` | NOT NULL | Tipo MIME |
| `expected_sha256` | `varchar(64)` | NOT NULL | Hash calculado no frontend |
| `actual_sha256` | `varchar(64)` | NULLABLE | Hash calculado após upload |
| `upload_scenario` | `varchar(10)` | NOT NULL | `TUS` ou `MINIO` |
| `storage_key` | `varchar(500)` | NULLABLE | Chave no storage (TUS fileId ou MinIO key) |
| `minio_upload_id` | `varchar(200)` | NULLABLE | ID do multipart (apenas MinIO) |
| `status` | `varchar(20)` | NOT NULL | `PENDING`, `COMPLETED`, `CORRUPTED`, `CANCELLED`, `FAILED` |
| `created_by` | `varchar(100)` | NOT NULL | Username do JWT |
| `created_at` | `timestamptz` | NOT NULL | Data de criação |
| `completed_at` | `timestamptz` | NULLABLE | Data de conclusão/falha |

**Índices:**
- `IX_file_uploads_status` em `(status)` — consultas do job de limpeza
- `IX_file_uploads_created_by` em `(created_by)` — listagem por usuário

**EF Core Configuration:**

```csharp
public class FileUploadConfiguration : IEntityTypeConfiguration<FileUpload>
{
    public void Configure(EntityTypeBuilder<FileUpload> builder)
    {
        builder.ToTable("file_uploads");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.FileName).HasMaxLength(500).IsRequired();
        builder.Property(f => f.FileSizeBytes).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(f => f.ExpectedSha256).HasMaxLength(64).IsRequired();
        builder.Property(f => f.ActualSha256).HasMaxLength(64);
        builder.Property(f => f.UploadScenario).HasMaxLength(10).IsRequired();
        builder.Property(f => f.StorageKey).HasMaxLength(500);
        builder.Property(f => f.MinioUploadId).HasMaxLength(200);
        builder.Property(f => f.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(f => f.CreatedBy).HasMaxLength(100).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();

        builder.HasIndex(f => f.Status).HasDatabaseName("IX_file_uploads_status");
        builder.HasIndex(f => f.CreatedBy).HasDatabaseName("IX_file_uploads_created_by");
    }
}
```

### Endpoints de API

**Autenticação:**

| Método | Path | Descrição | Auth |
|---|---|---|---|
| `POST` | `/api/v1/auth/login` | Login com credenciais fixas → retorna JWT (8h) | Não |

**Upload TUS:**

| Método | Path | Descrição | Auth |
|---|---|---|---|
| `POST` | `/api/v1/uploads/tus/register` | Registra metadados (nome, tamanho, SHA-256) → cria `PENDENTE` no DB → retorna `uploadId` e `storageKey` | JWT |
| `POST/PATCH/HEAD` | `/upload/tus` | Endpoint TUS protocol (gerenciado por `tusdotnet`) | JWT (manual) |
| `DELETE` | `/api/v1/uploads/{id}/cancel` | Cancela upload → status `CANCELADO` → remove arquivo parcial | JWT |

**Upload MinIO:**

| Método | Path | Descrição | Auth |
|---|---|---|---|
| `POST` | `/api/v1/uploads/minio/initiate` | Registra metadados + inicia multipart no MinIO → retorna `uploadId`, pre-signed URLs | JWT |
| `POST` | `/api/v1/uploads/minio/complete` | Recebe ETags → `CompleteMultipartUpload` → publica evento | JWT |
| `DELETE` | `/api/v1/uploads/minio/abort` | `AbortMultipartUpload` → status `CANCELADO` | JWT |

**Listagem e Download:**

| Método | Path | Descrição | Auth |
|---|---|---|---|
| `GET` | `/api/v1/files` | Lista todos os uploads com status, tamanho, data, checksum | JWT |
| `GET` | `/api/v1/files/{id}/download` | TUS: retorna arquivo com `enableRangeProcessing`. MinIO: redirect para pre-signed URL (1h) | JWT |

**Formato de erro:** RFC 9457 (Problem Details) em todas as respostas de erro.

---

## Pontos de Integração

### tusdotnet

- **Middleware**: mapeado em `/upload/tus` via `app.MapTus()`
- **Store**: `TusDiskStore` apontando para `/app/uploads/` (volume local no Docker Compose; PVC já provisionado no K8s)
- **Config**: `MaxAllowedUploadSizeInBytes = 300 GB`
- **Evento** `OnFileCompleteAsync`: dispara publicação no RabbitMQ
- **Autenticação**: validação manual do JWT no callback de configuração (o middleware TUS intercepta antes do pipeline de auth do ASP.NET Core)
- **Correlação com DB**: metadados TUS (`metadata.uploadId`) associam o `fileId` do TUS ao `Guid` do registro no PostgreSQL

### AWSSDK.S3 (MinIO)

- **Client**: `AmazonS3Client` com `ForcePathStyle = true` e `ServiceURL` apontando para o MinIO
- **Operações**: `InitiateMultipartUploadAsync`, `GetPreSignedURL` (PUT, 24h), `CompleteMultipartUploadAsync`, `AbortMultipartUploadAsync`
- **Download**: `GetPreSignedURL` (GET, 1h) com redirect 302
- **Lifecycle**: `PutLifecycleConfigurationAsync` no startup para expirar multipart incompletos em 3 dias
- **Tratamento de erros**: retry com Polly (3 tentativas, backoff exponencial) para chamadas ao MinIO

### RabbitMQ

- **Biblioteca**: `RabbitMQ.Client` (v6.x)
- **Exchange**: `upload-events` (type: direct, durable)
- **Queue principal**: `upload-completed-queue` (durable, routing key: `upload.completed`)
- **Dead-letter exchange**: `upload-events-dlx`
- **Dead-letter queue**: `upload-completed-dlq`
- **Publisher confirms**: habilitados via `ConfirmSelect()` + `WaitForConfirmsOrDie()`
- **Consumer**: manual ack (`BasicAck`) somente após persistir atualização no PostgreSQL
- **Retry**: 3 tentativas com `x-delivery-count`; após 3 falhas, mensagem vai para DLQ
- **Payload** (`UploadCompletedEvent`):

```json
{
  "uploadId": "guid",
  "storageKey": "string",
  "expectedSha256": "string",
  "uploadScenario": "TUS|MINIO",
  "timestamp": "ISO8601"
}
```

### PostgreSQL

- **Provider**: `Npgsql.EntityFrameworkCore.PostgreSQL`
- **Connection string** via environment variable (`ConnectionStrings__DefaultConnection`)
- **Migrations**: EF Core code-first, executadas automaticamente no startup (`Database.MigrateAsync()`)

---

## Análise de Impacto

| Componente Afetado | Tipo de Impacto | Descrição & Nível de Risco | Ação Requerida |
|---|---|---|---|
| **MinIO (cluster)** | Rede/Storage | Upload direto do browser exige que MinIO seja acessível via rede do cliente. Risco médio: CORS no MinIO | Solicitar configuração CORS ao time de infra |
| **RabbitMQ** | Mensageria | Nova exchange, queues e DLQ. Risco baixo: serviço já existe no cluster | Criar exchange/queues via startup da app ou script |
| **Browser (SHA-256)** | CPU/Memória | Cálculo SHA-256 de 250 GB no browser via Web Worker. Risco alto: pode levar 10-20 min e consumir memória | Implementar streaming com progresso; considerar tornar opcional acima de X GB |

---

## Abordagem de Testes

> **Nota:** Por se tratar de uma POC, a estratégia de testes é simplificada. Não há testes de integração com Testcontainers, E2E ou testes automatizados de frontend. O foco está em testes unitários da lógica de domínio e validação manual dos fluxos.

### Testes Unitários

**Framework:** xUnit + Moq + AwesomeAssertions (conforme `rules/dotnet-testing.md`)

**Componentes a testar:**
- `FileUpload` (transições de status: `Pending→Completed`, `Pending→Corrupted`, `Pending→Cancelled`, `Pending→Failed`)
- `UploadCompletedConsumer` (lógica de SHA-256 match/mismatch → update status)
- `FluentValidation` validators (tamanho máximo, SHA-256 format, nome obrigatório)

**Mocks:** `IFileUploadRepository`, `IStorageService`, `IEventPublisher`

**Cenários críticos:**
- SHA-256 match → status `COMPLETED`
- SHA-256 mismatch → status `CORRUPTED`
- Upload cancelado → arquivo removido do storage + status `CANCELLED`
- Timeout 24h → status `FAILED` + limpeza

### Testes Manuais (Checklist)

- Upload de arquivo ≥ 1 GB em ambos os cenários
- Pause/resume no TUS (desconectar rede, reconectar)
- Cancel no MinIO (verificar limpeza no bucket)
- Multi-pod: iniciar upload, escalar pods, verificar integridade
- Download com integridade SHA-256
- Verificar mensagens na DLQ via RabbitMQ Management UI
- Verificar logs de limpeza de órfãos

---

## Sequenciamento de Desenvolvimento

### Ordem de Construção

1. **Infraestrutura Docker Compose** — PostgreSQL, RabbitMQ, MinIO, backend, frontend. Garante ambiente funcional desde o início.
2. **Camada Domain** — Entidade `FileUpload`, enums, interfaces, eventos. Fundação sem dependências externas.
3. **Camada Infra: PostgreSQL** — `AppDbContext`, migrations, `FileUploadRepository`. Persistência first.
4. **Camada Infra: RabbitMQ** — `RabbitMqPublisher`, `RabbitMqConsumerHostedService`, configuração de DLQ.
5. **F01 — Autenticação JWT** — Endpoint login, middleware auth, JwtService. Desbloqueia todos os endpoints protegidos.
6. **F02 — Registro de Metadados** — `RegisterUploadHandler`, validação, persistência. Base para ambos os cenários.
7. **F04 — Upload MinIO** — `InitiateMinioUploadHandler`, `CompleteUploadHandler`, `MinioStorageService`. Cenário stateless primeiro (mais simples para validar pipeline completo).
8. **F03 — Upload TUS** — `tusdotnet` middleware, `TusDiskStorageService`, correlação com metadados DB.
9. **F02 cont. — Consumer + Integridade** — `UploadCompletedConsumer`, cálculo SHA-256, atualização de status.
10. **F05 — Listagem e Download** — `FilesController`, download TUS (Range), download MinIO (redirect).
11. **F06 — Detecção de Órfãos** — `OrphanCleanupJob` (Hosted Service com timer), DLQ consumer, lifecycle rules MinIO.
12. **Frontend completo** — Login, upload TUS, upload MinIO, listagem, download. Feature-based parallel.
13. **F07 — Kubernetes YAMLs** — Apenas Deployment, Service e Ingress do backend e frontend. PostgreSQL, RabbitMQ e MinIO já existem no cluster.
14. **Testes unitários** — Apenas domínio e consumer. Validação funcional manual.

### Dependências Técnicas

| Dependência | Bloqueante para |
|---|---|
| Docker Compose funcional | Todos os testes locais |
| PostgreSQL + EF Core migrations | Qualquer endpoint que persiste dados |
| RabbitMQ configurado (exchange, queues, DLQ) | Fluxo de conclusão de upload |
| MinIO acessível com CORS correto | Cenário MinIO (upload direto do browser) |
| PostgreSQL, RabbitMQ e MinIO disponíveis no cluster | Deploy dos YAMLs de K8s |

---

## Monitoramento e Observabilidade

Dado que esta é uma POC com coleta manual de métricas, a observabilidade é simplificada:

### Logging Estruturado

- **Framework**: Serilog com output JSON no console (conforme `rules/dotnet-logging.md`)
- **Campos obrigatórios**: `timestamp`, `level`, `message`, `service.name` = `upload-poc`
- **Logs críticos:**
  - `[INFO]` Upload registrado: `{ uploadId, fileName, scenario, fileSize }`
  - `[INFO]` Upload concluído: `{ uploadId, actualSha256, durationMs }`
  - `[WARN]` SHA-256 mismatch: `{ uploadId, expected, actual }`
  - `[WARN]` Upload timeout detectado: `{ uploadId, pendingSinceHours }`
  - `[ERROR]` Falha ao publicar evento: `{ uploadId, rmqError }`
  - `[ERROR]` Consumer falhou: `{ uploadId, attempt, error }`
  - `[INFO]` Órfão removido: `{ uploadId, action, storageKey }`

### Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql")
    .AddRabbitMQ(rabbitConnectionString, name: "rabbitmq")
    .AddUrlGroup(new Uri(minioHealthUrl), name: "minio");

app.MapHealthChecks("/health");
```

### Métricas Manuais (POC)

- Tempo total de upload (log no `OnFileComplete` com timestamp de início)
- CPU/RAM via `docker stats` durante upload
- Contagem de mensagens na DLQ via RabbitMQ Management UI

---

## Considerações Técnicas

### Decisões Principais

| Decisão | Justificativa | Alternativa Rejeitada |
|---|---|---|
| **Clean Architecture com 5 camadas** | Alinhamento com regras do projeto (`rules/dotnet-folders.md`). Separação clara facilita testes e evolução. | Monolito simples — descartado por preferência do time |
| **RabbitMQ.Client direto** | Controle total sobre publisher confirms, manual ack e DLQ. Sem overhead de abstrações para POC simples. | MassTransit — overhead desnecessário para 1 exchange/1 queue |
| **EF Core + Migrations** | Code-first alinhado com padrões do time. Uma tabela simples não justifica ORM alternativo. | Dapper — menos produtivo para migrations e tracking |
| **Feature-based React** | Separação clara entre cenários TUS e MinIO no frontend. Facilita comparação e remoção futura. | Estrutura flat — misturaria hooks/páginas dos dois cenários |
| **AWSSDK.S3 (não MinIO SDK)** | Recomendação oficial do MinIO. SDK único para qualquer S3-compatible. | Minio SDK .NET — tem bugs conhecidos e menor manutenção |
| **SHA-256 via Web Worker** | Não bloqueia UI. Permite progresso visual durante cálculo. | SHA-256 no main thread — congelaria UI por minutos em 250 GB |
| **tusdotnet OnFileCompleteAsync → RabbitMQ** | Desacopla conclusão do upload da validação de integridade. Resiliência via DLQ. | Validar inline no callback — sem retry; falha perde o evento |
| **Job periódico como IHostedService** | Simples para POC. Timer configurável. Sem dependência externa (Hangfire/Quartz). | Hangfire — infraestrutura extra desnecessária para 1 job |

### Riscos Conhecidos

| Risco | Impacto | Mitigação |
|---|---|---|
| SHA-256 de 250 GB no browser: 10-20 min | UX ruim, usuário pode cancelar | Web Worker + barra de progresso; considerar tornar opcional ou calcular server-side como fallback |
| `tusdotnet` valida JWT manualmente | Código de auth duplicado, risco de bypass | Extrair para `JwtService.ValidateToken()` compartilhado; documentar claramente |
| RabbitMQ indisponível no momento do publish | Upload completo mas status fica `PENDING` eternamente | Publisher confirms + retry com Polly; job de limpeza como safety net |
| PVC ReadWriteMany já provisionado | Possível degradação de performance vs. local disk | Validar com 2 pods simultâneos no cluster; documentar throughput |
| CORS no MinIO para upload direto | Browser pode bloquear PUT para minIO | Configurar CORS no bucket via `mc` CLI ou via API no startup |
| Pre-signed URL expira antes do upload terminar | Uploads de 250 GB com rede lenta podem levar >24h | URLs com 24h de validade; retry individual por chunk |

### Requisitos Especiais

**Performance:**
- Chunks de 100 MB balanceiam paralelismo vs. overhead de conexão
- MinIO: 5 chunks simultâneos (10 poderia saturar rede doméstica; 5 é conservador)
- TUS: sequential por design do protocolo, compensado pelo resumability
- Backend MinIO: ~2 requisições (initiate + complete) → CPU/RAM mínimos

**Segurança:**
- JWT com HMAC-SHA256, validade 8h, chave ≥32 chars via environment variable
- Pre-signed URLs não expõem credenciais do MinIO (assinadas com chave secreta)
- Sem token na URL de download em produção (usar cookies HttpOnly)

**CORS (MinIO):**

```bash
mc anonymous set none local/uploads
# CORS via mc ou via API:
# AllowedOrigins: ["http://localhost:3000"]
# AllowedMethods: ["PUT"]
# AllowedHeaders: ["*"]
# ExposeHeaders: ["ETag"]
```

### Conformidade com Padrões

- **Clean Architecture** (`rules/dotnet-architecture.md`): 5 camadas com inversão de dependências
- **Coding standards** (`rules/dotnet-coding-standards.md`): PascalCase, métodos ≤50 linhas, classes ≤300 linhas, código em inglês
- **REST** (`rules/restful.md`): versionamento na URL (`/api/v1/`), RFC 9457 para erros, paginação na listagem
- **Logging** (`rules/dotnet-logging.md`): JSON estruturado com Serilog, campos obrigatórios
- **Observabilidade** (`rules/dotnet-observability.md`): Health checks para PostgreSQL, RabbitMQ, MinIO
- **Testing** (`rules/dotnet-testing.md`): xUnit + Moq + AwesomeAssertions, padrão AAA (apenas unitários para a POC)
- **React** (`rules/react-coding-standards.md`): PascalCase componentes, camelCase hooks, TypeScript strict
- **React structure** (`rules/react-project-structure.md`): feature-based para projeto com múltiplos fluxos

---

## Dependências NuGet (Backend)

```xml
<!-- 1-Services (API) -->
<PackageReference Include="tusdotnet" Version="2.8.1" />
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.0" />
<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="7.0.0" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="6.5.0" />
<PackageReference Include="Serilog.AspNetCore" Version="8.0.0" />
<PackageReference Include="Serilog.Sinks.Console" Version="5.0.1" />
<PackageReference Include="Serilog.Formatting.Compact" Version="2.0.0" />

<!-- 2-Application -->
<PackageReference Include="FluentValidation" Version="11.8.1" />
<PackageReference Include="FluentValidation.AspNetCore" Version="11.3.0" />

<!-- 4-Infra -->
<PackageReference Include="AWSSDK.S3" Version="3.7.305" />
<PackageReference Include="RabbitMQ.Client" Version="6.8.1" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="8.0.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.0" />
<PackageReference Include="Polly" Version="8.2.0" />
<PackageReference Include="AspNetCore.HealthChecks.NpgSql" Version="7.1.0" />
<PackageReference Include="AspNetCore.HealthChecks.Rabbitmq" Version="7.1.0" />

<!-- 5-Tests (apenas unitários) -->
<PackageReference Include="xunit" Version="2.6.6" />
<PackageReference Include="Moq" Version="4.20.70" />
<PackageReference Include="AwesomeAssertions" Version="6.15.1" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
```

## Dependências npm (Frontend)

```json
{
  "dependencies": {
    "react": "^18.3.0",
    "react-dom": "^18.3.0",
    "react-router-dom": "^6.20.0",
    "tus-js-client": "^4.1.0",
    "axios": "^1.6.0"
  },
  "devDependencies": {
    "typescript": "^5.3.0",
    "vite": "^5.0.0",
    "@vitejs/plugin-react": "^4.2.0"
  }
}
```

## Docker Compose

```yaml
version: "3.9"
services:
  postgresql:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: uploadpoc
      POSTGRES_USER: poc
      POSTGRES_PASSWORD: poc123
    ports:
      - "5432:5432"
    volumes:
      - pg_data:/var/lib/postgresql/data

  rabbitmq:
    image: rabbitmq:3.13-management-alpine
    ports:
      - "5672:5672"
      - "15672:15672"
    environment:
      RABBITMQ_DEFAULT_USER: poc
      RABBITMQ_DEFAULT_PASS: poc123

  minio:
    image: minio/minio:latest
    command: server /data --console-address ":9001"
    environment:
      MINIO_ROOT_USER: minioadmin
      MINIO_ROOT_PASSWORD: minioadmin123
    ports:
      - "9000:9000"
      - "9001:9001"
    volumes:
      - minio_data:/data

  minio-setup:
    image: minio/mc:latest
    depends_on:
      minio:
        condition: service_healthy
    entrypoint: >
      /bin/sh -c "
        mc alias set local http://minio:9000 minioadmin minioadmin123;
        mc mb --ignore-existing local/uploads;
        exit 0;
      "

  backend:
    build: ./backend
    depends_on:
      - postgresql
      - rabbitmq
      - minio
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgresql;Database=uploadpoc;Username=poc;Password=poc123"
      Jwt__Secret: "poc-jwt-secret-minimo-32-caracteres-aqui!!"
      MinIO__Endpoint: "minio:9000"
      MinIO__AccessKey: "minioadmin"
      MinIO__SecretKey: "minioadmin123"
      MinIO__BucketName: "uploads"
      RabbitMQ__Host: "rabbitmq"
      RabbitMQ__Username: "poc"
      RabbitMQ__Password: "poc123"
      TusStorage__Path: "/app/uploads"
    ports:
      - "5000:8080"
    volumes:
      - tus_data:/app/uploads

  frontend:
    build: ./frontend
    depends_on:
      - backend
    ports:
      - "3000:80"

volumes:
  pg_data:
  minio_data:
  tus_data:
```
