# Review da Task 8.0 — Upload TUS Backend (F03)

## 1) Resultados da validacao da definicao da tarefa

Base analisada:
- Task: `tasks/prd-upload-arquivos-grandes/8_task.md`
- PRD: `tasks/prd-upload-arquivos-grandes/prd.md`
- Tech Spec: `tasks/prd-upload-arquivos-grandes/techspec.md`

Arquivos revisados (obrigatorios):
- `backend/4-Infra/UploadPoc.Infra/Storage/TusDiskStorageService.cs`
- `backend/1-Services/UploadPoc.API/Program.cs`
- `backend/1-Services/UploadPoc.API/Controllers/TusUploadController.cs`

Conclusao de conformidade por requisito principal da task 8.0:
- RF03.1 / RF03.3 / RF03.6: **Atendido** via `app.MapTus("/upload/tus")`, `TusDiskStore`, limite de 300 GB e fluxo TUS resumable.
- RF03.5 / RF03.7: **Atendido apos ajuste nesta review** — cancelamento TUS agora tenta remover arquivo parcial via storage keyed (`tus-disk`) no `CancelUploadHandler`.
- JWT manual no middleware TUS (`OnAuthorizeAsync`): **Atendido**.
- Correlacao `uploadId` -> `StorageKey` em `OnCreateCompleteAsync`: **Atendido**.
- Publicacao de `UploadCompletedEvent` em `OnFileCompleteAsync`: **Atendido**.
- Documentacao do fluxo no controller: **Atendido**.

## 2) Descobertas da analise de skills

Skills carregadas e aplicadas:
- `dotnet-production-readiness`
- `dotnet-architecture`
- `dotnet-code-quality`
- `restful-api`

Achados por skill:
- `dotnet-code-quality` / `dotnet-architecture`
  - Encontrado e corrigido: cancelamento TUS nao removia parcial em disco (gap funcional + acoplamento incompleto com keyed service).
  - Encontrado e corrigido: validacao de path em disco vulneravel a bypass por prefixo de path (`StartsWith` sem separador).
- `restful-api`
  - Endpoints permanecem versionados em `/api/v1/...` e o endpoint TUS fora de controller foi documentado corretamente.
- `dotnet-production-readiness`
  - Observacao nao bloqueante: stack usa Serilog; skill recomenda baseline OpenTelemetry/OTLP para producao. Nao e escopo direto da task 8.0.

## 3) Resumo da revisao de codigo

Pontos fortes:
- Configuracao do `tusdotnet` no `Program.cs` esta aderente ao desenho da task (auth manual, correlacao e evento de conclusao).
- Fluxo de evento em `OnFileCompleteAsync` persiste (`UpdateAsync`) antes de publicar evento, alinhado ao criterio de event flow.
- Registro DI keyed para storage TUS presente.

Correcoes aplicadas nesta revisao:
1. `backend/2-Application/UploadPoc.Application/Handlers/CancelUploadHandler.cs`
   - Adicionado suporte a limpeza de arquivo parcial para cenario `TUS` usando `IKeyedServiceProvider` e key `"tus-disk"`.
2. `backend/4-Infra/UploadPoc.Infra/Storage/TusDiskStorageService.cs`
   - Endurecida validacao contra path traversal:
     - bloqueio de separadores de diretorio no `storageKey`;
     - validacao de prefixo com separador de diretorio para evitar falso positivo de `StartsWith`.

Observacoes remanescentes (nao bloqueantes para task 8.0):
- `OnCreateCompleteAsync` retorna silenciosamente quando metadado `uploadId` esta ausente/invalido ou quando upload nao existe. Isso pode gerar arquivo sem correlacao imediata com DB e depende de reconciliacao/cleanup posterior.

## 4) Problemas enderecados e resolucoes

- **Alta severidade (seguranca):** risco de path traversal em `TusDiskStorageService.GetFilePath`.
  - **Resolucao:** validacao de `storageKey` sem separadores + comparacao de prefixo com separador.
- **Alta severidade (compliance RF03.7):** cancelamento TUS nao removia arquivo parcial.
  - **Resolucao:** `CancelUploadHandler` agora resolve e usa storage keyed `tus-disk` para `DeleteAsync` quando o cenario e TUS.

## 5) Status

**APPROVED WITH OBSERVATIONS**

## 6) Confirmacao de conclusao da tarefa e prontidao para deploy

- Build executado com sucesso: `dotnet build UploadPoc.sln`.
- Testes automatizados: `dotnet test UploadPoc.sln` **nao executaram** por incompatibilidade de runtime do ambiente (somente .NET 10 instalado; projeto em .NET 8). Restricao previamente aceita no contexto da review.
- Nao houve commit e `tasks.md` nao foi alterado.
- Com as correcoes aplicadas e sem bloqueadores funcionais da task 8.0, a implementacao fica **pronta para avancar**, com observacoes registradas.
