# Review da Task 9.0 — Consumer de Integridade SHA-256 (Re-review)

## 1) Resultados da validacao da definicao da tarefa

- **Escopo validado**:
  - `tasks/prd-upload-arquivos-grandes/9_task.md`
  - `tasks/prd-upload-arquivos-grandes/techspec.md`
  - `backend/2-Application/UploadPoc.Application/Consumers/UploadCompletedConsumer.cs`
  - `backend/4-Infra/UploadPoc.Infra/Services/Sha256ChecksumService.cs`
  - `backend/4-Infra/UploadPoc.Infra/Messaging/RabbitMqConsumerHostedService.cs`
  - `backend/1-Services/UploadPoc.API/Program.cs`
  - `backend/3-Domain/UploadPoc.Domain/Interfaces/IStorageService.cs`
  - `backend/4-Infra/UploadPoc.Infra/Storage/MinioStorageService.cs`
  - `backend/4-Infra/UploadPoc.Infra/Storage/TusDiskStorageService.cs`

- **Aderencia aos requisitos da Task 9.0 / RF02**:
  - RF02.4 (consumer processa `upload.completed`, valida integridade e atualiza status): **OK**.
  - RF02.5 (divergencia SHA-256 marca `Corrupted`): **OK**.
  - RF02.7 (`BasicAck` apenas apos persistencia): **OK**.
  - RF02.8 (falhas seguem para DLQ): **OK**, com politica de retry antes do envio final.
  - Subtarefa 9.2 (`Sha256ChecksumService` com streaming e buffer 8KB): **OK**.
  - Subtarefa 9.4 (registro DI de consumer/checksum/storage keyed): **OK**.

- **Revalidacao dos problemas anteriores**:
  1. Retry RabbitMQ antes de DLQ: **CORRIGIDO**.
  2. Calculo de hash via `IStorageService.ComputeSha256Async`: **CORRIGIDO**.
  3. Testes unitarios ausentes na task 9.0: **NAO BLOQUEANTE** (escopo de testes formalmente na task 17.0).

## 2) Descobertas da analise de SKILLs

### Skills carregadas e aplicadas

- `dotnet-production-readiness`
- `dotnet-code-quality`
- `dotnet-architecture`
- `dotnet-observability`

### Validacao por regras das skills

- **dotnet-architecture**: separacao de responsabilidades preservada (consumer na Application, calculo de hash encapsulado no storage da Infra), DI por contratos de dominio e keyed services consistente.
- **dotnet-code-quality**: codigo com nomenclatura consistente, async com `CancellationToken`, logging estruturado com templates e baixo acoplamento apos a refatoracao.
- **dotnet-observability**: logs de sucesso/falha e mismatch com contexto de `UploadId`, cenario e hashes; fluxo de erro no consumer com log de tentativa e envio a DLQ apos maximo.
- **dotnet-production-readiness**: comportamento resiliente no consumo RabbitMQ com retentativas, ack manual e caminho de falha claramente definido.

### Violacoes encontradas

- **Nenhuma violacao critica ou alta** no escopo da Task 9.0.
- **Observacao nao bloqueante**: `dotnet test` segue abortando por limitacao de runtime no ambiente local (falta `Microsoft.NETCore.App 8.0.0`).

## 3) Resumo da revisao de codigo

- `UploadCompletedConsumer` agora delega o checksum para o storage selecionado por cenario (`TUS`/`MINIO`) via `ComputeSha256Async`, conforme task/techspec.
- `RabbitMqConsumerHostedService` implementa contagem por header (`x-delivery-count`) e retry ate o limite (`MaxDeliveryAttempts = 3`), com republicacao e `BasicAck` da mensagem original quando retry e enfileirado.
- Ao exceder tentativas, o consumer realiza `BasicNack(... requeue: false)`, permitindo roteamento para DLQ conforme configuracao da infraestrutura RabbitMQ.
- `IStorageService`, `MinioStorageService` e `TusDiskStorageService` estao alinhados com a abstracao de checksum por storage.
- `Program.cs` registra corretamente `UploadCompletedConsumer`, `IChecksumService` e keyed services (`"tus-disk"`, `"minio"`).

### Build/Test executados

- `dotnet build UploadPoc.sln`: **SUCESSO** (apenas warnings de resolucao NuGet).
- `dotnet test UploadPoc.sln`: **ABORTADO por limitacao de ambiente** (runtime .NET 8.0.0 ausente; somente 10.0.3 disponivel). Conforme orientacao desta re-review, **nao bloqueia aprovacao**.

## 4) Problemas enderecados e resolucoes

1. **Retry antes de DLQ (previo blocker)**
   - **Status**: Resolvido.
   - **Como foi resolvido**: implementada leitura de header de tentativa (`x-delivery-count`), republicacao com incremento de tentativa, e envio para DLQ apenas ao atingir maximo de 3 tentativas.

2. **Delegacao do checksum para storage (previo blocker)**
   - **Status**: Resolvido.
   - **Como foi resolvido**: `UploadCompletedConsumer` usa `IStorageService.ComputeSha256Async(storageKey, ct)` diretamente.

3. **Ausencia de testes unitarios na task 9.0**
   - **Status**: Nao bloqueante para esta task.
   - **Justificativa**: escopo de testes foi deslocado para a Task 17.0 no contexto desta revisao.

## 5) Status

**APPROVED WITH OBSERVATIONS**

## 6) Conclusao da tarefa e prontidao para deploy

- A Task 9.0 esta **concluida no escopo funcional revisado** e apta para seguir no fluxo.
- Ha **prontidao tecnica para deploy da feature**, condicionada apenas aos processos normais de pipeline/ambiente (incluindo execucao de testes em ambiente com runtime .NET 8 compativel).
