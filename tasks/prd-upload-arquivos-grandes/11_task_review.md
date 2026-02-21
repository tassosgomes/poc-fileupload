# Task 11.0 Review — Detecção e Limpeza de Dados Órfãos (F06)

## 1) Resultados da validação da definição da tarefa

- **Aderência geral:** Implementação cobre os requisitos centrais de F06 (job periódico, publicação de timeout, processamento de DLQ, limpeza de órfãos TUS, abort multipart MinIO, configuração em startup).
- **RF06.1 / RF06.2:** `OrphanCleanupJob` implementado como `BackgroundService`, com `IntervalMinutes` e `TimeoutHours` configuráveis, detectando `Pending` antigos e publicando `upload.timeout`.
- **RF06.3:** Consumer DLQ implementado e ativo no `RabbitMqConsumerHostedService`, com reconciliação via `ExistsAsync`, validação de tamanho/sha e atualização de status.
- **RF06.4:** Lifecycle MinIO para abort de multipart incompleto em 3 dias configurada em `MinioStorageService.ConfigureBucketAsync`.
- **RF06.5:** Detecção de órfãos TUS por varredura de disco + verificação em PostgreSQL (`ExistsByStorageKeyAsync`) com remoção de arquivo e metadados.
- **RF06.6 / RF06.7:** Configuração automatizada no startup e logging de ações de limpeza com dados auditáveis.

## 2) Skills carregadas e violações encontradas

### Skills carregadas
- `dotnet-production-readiness` (prioritária)
- `dotnet-architecture`
- `dotnet-code-quality`
- `dotnet-observability`
- `dotnet-dependency-config`

### Skills não aplicadas (não relevantes nesta task)
- `restful-api` (sem novo endpoint REST nesta implementação)
- `roles-naming` (sem alterações de roles/perfis)

### Violações / observações por skill
- **dotnet-production-readiness / dotnet-observability:** Observabilidade está funcional (health checks/logs), porém sem baseline OpenTelemetry OTLP completo (tracing + logging OTLP), ficando abaixo do padrão de prontidão recomendado pela skill.
- **dotnet-dependency-config:** Warnings de restore (`NU1603`) indicam drift de versões de pacotes (RabbitMQ HealthChecks e AwesomeAssertions).
- **dotnet-code-quality:** Logging está majoritariamente estruturado, mas há templates com formatação menos padronizada em alguns pontos.

## 3) Resumo da revisão de código

- **OrphanCleanupJob:** Correto como hosted background job, com `PeriodicTimer`, escopo de DI por ciclo, tratamento de cancelamento e captura de exceções.
- **Detecção TUS órfão:** Fluxo implementado com exclusão de metadados via `TusDiskStorageService.DeleteAsync` e lookup eficiente por `ExistsByStorageKeyAsync` (evita `GetAllAsync`).
- **Consumer DLQ:** Fluxo cobre upload inexistente, upload já finalizado, storage ausente/incompleto, validação SHA-256 e limpeza em cenários de falha.
- **MinIO multipart abort:** Chamado corretamente no fluxo MINIO quando há `MinioUploadId`.
- **Publisher timeout:** `PublishUploadTimeoutAsync` publica no `upload-events-dlx` com routing key `upload.timeout`; bindings da infra suportam consumo na DLQ.
- **DI registration:** `OrphanCleanupJob`, `UploadCompletedDlqConsumer`, storages keyed e services de mensageria estão registrados corretamente em `Program.cs`.

## 4) Problemas endereçados e resoluções

1. **[Corrigido] Inconsistência de status para SHA mismatch no DLQ**
   - **Problema:** No fluxo DLQ, divergência de SHA estava indo para `Failed` com limpeza, em desacordo com a semântica de integridade (`Corrupted`) usada no fluxo principal.
   - **Resolução aplicada:** Ajustado `UploadCompletedDlqConsumer` para marcar `Corrupted` em mismatch (`MarkCorrupted`) e persistir o status.
   - **Arquivo:** `backend/2-Application/UploadPoc.Application/Consumers/UploadCompletedDlqConsumer.cs`

2. **[Observação] Reconciliação DLQ em caso de arquivo íntegro**
   - **Achado:** No caso íntegro, o consumer DLQ marca `Completed` diretamente (não república `upload.completed`).
   - **Impacto:** Funcionalmente resolve a reconciliação, mas difere do fluxo descrito no texto da tarefa/PRD que sugere republicação de evento de conclusão.
   - **Recomendação:** Padronizar decisão arquitetural (republicar evento vs. finalizar direto) e documentar explicitamente para evitar ambiguidade operacional.

3. **[Observação] Warnings de dependência no build/test**
   - **Achado:** `NU1603` para versões não resolvidas exatamente.
   - **Recomendação:** Fixar versões realmente disponíveis ou atualizar baseline dos `.csproj` para eliminar drift.

## 5) Status final

**APPROVED WITH OBSERVATIONS**

## 6) Conclusão e prontidão para deploy

- A implementação da Task 11.0 está **funcionalmente concluída** para os requisitos de F06 e **pronta para deploy** em ambiente de POC.
- Há **observações não bloqueantes** de padronização arquitetural (republicação no DLQ) e higiene de dependências (warnings `NU1603`) recomendadas para hardening.

## Evidências de validação executadas

- `dotnet build backend/UploadPoc.sln` → **Sucesso** (0 erros, warnings de dependência)
- `dotnet test backend/UploadPoc.sln` → **Abortado por runtime mismatch** (`Microsoft.NETCore.App 8.0.0` ausente no ambiente; apenas 10.0.3 disponível) — limitação aceita conforme instrução.
