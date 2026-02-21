# Review da Tarefa 5.0 — Autenticacao JWT (F01)

## 1) Resultados da validacao da definicao da tarefa

- **Escopo validado**: `tasks/prd-upload-arquivos-grandes/5_task.md` e `tasks/prd-upload-arquivos-grandes/techspec.md`.
- **RF01.1 (login com credenciais fixas + JWT 8h)**: **Atendido** em `backend/1-Services/UploadPoc.API/Controllers/AuthController.cs` e `backend/1-Services/UploadPoc.API/Services/JwtService.cs`.
- **RF01.2/RF01.3 (protecao via Bearer token)**: **Atendido no escopo atual** com `AddAuthentication`, `AddJwtBearer`, `AddAuthorization` e `[Authorize]` no controller existente.
- **RFC 9457 (Problem Details)**: **Atendido** para credenciais invalidas, challenge de JWT e excecoes nao tratadas.
- **Subtarefa 5.3 (JwtService com GenerateToken/ValidateToken + Singleton)**: **Atendido**.
- **Subtarefa 5.5 (ExceptionHandlingMiddleware)**: **Atendido**.
- **Subtarefa 5.6 (Serilog JSON + service.name)**: **Atendido** (Compact JSON + enrichment `service.name=upload-poc`).
- **Subtarefa 5.7 (Health Checks PostgreSQL/RabbitMQ/MinIO)**: **Ajustado durante review** (faltava MinIO).
- **Subtarefa 5.8 (teste via Swagger e validacao 401 sem token)**: **Parcialmente evidenciado**; nao foi possivel executar testes automatizados no ambiente por ausencia do runtime .NET 8 para testhost.

## 2) Descobertas da analise de skills

### Skills carregadas
- `dotnet-production-readiness` (prioritaria)
- `dotnet-architecture`
- `dotnet-code-quality`
- `dotnet-observability`
- `restful-api`

### Skill nao carregada
- `roles-naming` **nao aplicavel** (nao ha roles/perfis na implementacao revisada).

### Violacoes encontradas e avaliacao
1. **[Alta]** Ausencia de health check de MinIO em `Program.cs` (requisito 5.7 / techspec).
2. **[Media]** Endurecimento incompleto da validacao JWT (nao restringia explicitamente algoritmo valido e expiracao obrigatoria).
3. **[Media]** Testes nao executaveis no ambiente atual por falta de runtime `Microsoft.NETCore.App 8.0.0` (build compila, mas `dotnet test` aborta).
4. **[Baixa]** Warnings NU1603 de resolucao de versao de pacotes (nao bloqueante para compilacao).

## 3) Resumo da revisao de codigo

- `backend/1-Services/UploadPoc.API/Controllers/AuthController.cs`: endpoint `POST /api/v1/auth/login` correto, credenciais POC fixas, retorno de token e Problem Details 401 para falha.
- `backend/1-Services/UploadPoc.API/Services/JwtService.cs`: geracao de token com claims principais (`Name`, `sub`, `jti`), assinatura HMAC-SHA256 e validacao com issuer/audience/lifetime/signing key.
- `backend/1-Services/UploadPoc.API/Middleware/ExceptionHandlingMiddleware.cs`: captura excecoes nao tratadas, log estruturado e resposta `application/problem+json`.
- `backend/1-Services/UploadPoc.API/Program.cs`: configuracoes de auth JWT, Serilog, middleware de excecao, health checks e mapeamento de endpoints.

## 4) Lista de problemas enderecados e resolucoes

1. **MinIO health check ausente**
   - **Resolucao**: adicionado check assicrono para endpoint de saude do MinIO (`/minio/health/live`) em `Program.cs`.
   - **Impacto**: conformidade com subtarefa 5.7 e com `dotnet-observability`.

2. **Hardening de validacao JWT**
   - **Resolucao**: adicionados `RequireExpirationTime = true` e `ValidAlgorithms = [SecurityAlgorithms.HmacSha256]` em `Program.cs` e `JwtService.ValidateToken`.
   - **Impacto**: melhora de seguranca conforme requisito de validacao JWT da tarefa.

3. **Validacoes defensivas de configuracao JWT**
   - **Resolucao**: `JwtService` passou a validar issuer, audience e `expirationHours > 0` no construtor.
   - **Impacto**: reduz risco de configuracao invalida em runtime.

## 5) Status final

**APROVADO COM OBSERVACOES**

## 6) Confirmacao de conclusao da tarefa e prontidao para deploy

- Implementacao da tarefa 5.0 esta **funcionalmente alinhada** aos requisitos principais e com os ajustes de review aplicados.
- **Observacao obrigatoria pre-deploy**: executar `dotnet test` em ambiente com runtime .NET 8 instalado (ou pipeline CI apropriado) e concluir validacao manual da subtarefa 5.8 via Swagger (login + 401 sem token nos endpoints protegidos).
- Com essas validacoes finais, a tarefa fica pronta para seguir fluxo de deploy.
