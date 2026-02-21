# PRD — POC Upload de Arquivos Grandes (TUS + MinIO)

## Visão Geral

Esta POC valida duas estratégias distintas para upload de arquivos grandes (até 250 GB) em ambiente on-premise com Kubernetes: o **protocolo TUS** (upload resumable via backend) e o **MinIO Multipart Upload** (upload direto via pre-signed URLs). O objetivo é fornecer evidências técnicas — de performance, escalabilidade, resiliência e complexidade — para embasar a decisão arquitetural de qual abordagem adotar em produção.

O problema central é que arquivos de 250 GB não podem ser enviados em uma única requisição HTTP. Qualquer instabilidade de rede causaria perda total do progresso, e manter os bytes em memória no backend é inviável. Ambos os cenários resolvem esse problema com chunking (fatiamento do arquivo), mas diferem fundamentalmente em onde os bytes trafegam e como o backend participa do fluxo.

**Premissa crítica de negócio:** cada arquivo vale $1 milhão. Dados órfãos (uploads que ficam no storage sem rastreamento no banco) são inaceitáveis. O sistema deve garantir consistência total entre o storage (disco/MinIO) e o registro no banco de dados, com mecanismos de detecção e limpeza automática de inconsistências.

### Fluxo de Ciclo de Vida do Upload

```
┌───────────┐   metadata   ┌──────────┐   chunks    ┌───────────┐
│  Cliente  │ ───────────► │ Backend  │ ──────────► │ Storage   │
│  (React)  │              │ (.NET 8) │             │(TUS/MinIO)│
└───────────┘              └────┬─────┘             └───────────┘
                               │
                    ┌──────────▼──────────┐
                    │    PostgreSQL       │
                    │  status: PENDENTE   │
                    └─────────────────────┘
                               │
              upload completo  │
                               ▼
                    ┌─────────────────────┐
                    │     RabbitMQ        │
                    │  evento: completed  │
                    └────────┬────────────┘
                             │
                             ▼
                    ┌─────────────────────┐
                    │    PostgreSQL       │
                    │  status: CONCLUÍDO  │
                    └─────────────────────┘
```

1. Cliente inicia upload → envia metadados ao backend → registro criado no PostgreSQL com status **PENDENTE**
2. Chunks são enviados (via TUS ou MinIO) com progresso rastreado
3. Ao completar o upload → backend publica evento no **RabbitMQ** → consumer atualiza status para **CONCLUÍDO**
4. Se o evento falhar → dead-letter queue + timeout detecta uploads pendentes eternamente → limpeza automática

**Stack tecnológica:** .NET 8 · React + Vite · PostgreSQL · RabbitMQ · Docker Compose · Kubernetes on-premise (Longhorn) · JWT

---

## Objetivos

1. **Validar viabilidade técnica** de ambos os cenários (TUS e MinIO) para upload de arquivos de até 250 GB em ambiente on-premise
2. **Comparar métricas objetivas** entre os cenários: tempo total de upload, consumo de CPU/RAM no backend, comportamento sob falha de rede e facilidade de escalabilidade horizontal
3. **Demonstrar resiliência**: comprovar que uploads interrompidos podem ser retomados (TUS) ou que uploads parciais são cancelados de forma limpa (MinIO)
4. **Validar operação em Kubernetes**: confirmar que ambos os cenários funcionam com múltiplos pods — TUS com volume compartilhado (PVC RWX via Longhorn) e MinIO com backend stateless
5. **Garantir zero dados órfãos**: validar que o ciclo de vida do upload (PENDENTE → CONCLUÍDO) é mantido consistente entre PostgreSQL e storage, com mecanismos de dead-letter e timeout para tratar falhas
6. **Validar integridade via SHA-256**: comprovar que o checksum do arquivo enviado confere com o checksum calculado após a consolidação no storage
7. **Produzir relatório comparativo** fundamentado em evidências para decisão arquitetural da equipe

### Métricas de Sucesso

| Métrica | Critério |
|---|---|
| Upload completo de arquivo ≥ 1 GB | Ambos os cenários concluem sem erro |
| Retomada após falha de rede (TUS) | Upload continua do ponto de interrupção, sem reinício |
| Cancelamento limpo (MinIO) | Chunks orphaned são removidos e não consomem storage permanente |
| Backend não processa bytes (MinIO) | CPU/RAM do backend permanecem estáveis durante upload de arquivo grande |
| Multi-pod funcional (K8s) | Upload iniciado em um pod e completado em outro sem corrupção |
| Download funcional | Arquivo baixado é idêntico ao original (integridade via SHA-256) |
| Zero dados órfãos | Nenhum arquivo no storage sem registro correspondente no PostgreSQL |
| Evento de conclusão resiliente | Falha no RabbitMQ → dead-letter → retry → arquivo é reconciliado |
| Transição de status consistente | Todo upload segue: PENDENTE → CONCLUÍDO (ou PENDENTE → FALHA → limpeza) |

---

## Histórias de Usuário

### Persona 1: Engenheiro de Plataforma (avaliador da POC)

- Como engenheiro de plataforma, eu quero **executar ambos os cenários de upload** em ambiente local (Docker Compose) e em Kubernetes para que eu possa **coletar métricas comparativas e recomendar a abordagem mais adequada** para produção.
- Como engenheiro de plataforma, eu quero **simular falhas de rede durante o upload** para que eu possa **validar a resiliência de cada abordagem** e documentar o comportamento.
- Como engenheiro de plataforma, eu quero **escalar o backend para múltiplos pods** para que eu possa **verificar se ambos os cenários funcionam sem corrupção** de dados em ambiente distribuído.
- Como engenheiro de plataforma, eu quero **simular falha no RabbitMQ** para que eu possa **validar que o mecanismo de dead-letter e timeout** detecta e trata uploads pendentes corretamente.
- Como engenheiro de plataforma, eu quero **verificar que não existem dados órfãos** no storage após cenários de falha para que eu tenha **confiança na integridade do sistema**.

### Persona 2: Usuário Final (simulação de uso real)

- Como usuário, eu quero **selecionar um arquivo grande e acompanhar o progresso do upload em tempo real** para que eu saiba **quanto falta para completar** e tenha visibilidade do processo.
- Como usuário, eu quero **pausar e retomar um upload em andamento** (cenário TUS) para que eu possa **gerenciar minha banda de rede** sem perder o progresso.
- Como usuário, eu quero **cancelar um upload a qualquer momento** para que **recursos de storage não sejam desperdiçados** com uploads indesejados.
- Como usuário, eu quero **listar os arquivos que já enviei e ver o status de cada um** (pendente, concluído) para que eu possa **confirmar que o upload foi processado com sucesso**.
- Como usuário, eu quero **fazer download de arquivos concluídos** para que eu possa **verificar a integridade** e recuperar meus arquivos.

---

## Funcionalidades Principais

### F01 — Autenticação JWT

Autenticação simplificada via JWT para proteger todos os endpoints da API. O usuário faz login com credenciais fixas (POC), recebe um token e o utiliza em todas as requisições subsequentes.

- **RF01.1**: O sistema deve expor um endpoint de login que aceite usuário e senha e retorne um token JWT válido por 8 horas.
- **RF01.2**: Todos os endpoints de upload, listagem e download devem rejeitar requisições sem token JWT válido (HTTP 401).
- **RF01.3**: O token JWT deve ser enviado no header `Authorization: Bearer <token>` em todas as requisições autenticadas.

### F02 — Registro de Metadados e Controle de Status

Antes do upload de bytes começar, o backend registra os metadados do arquivo no PostgreSQL com status PENDENTE. Ao final do upload, um evento no RabbitMQ aciona a transição para CONCLUÍDO. Este fluxo garante rastreabilidade completa e impede dados órfãos.

- **RF02.1**: Ao iniciar um upload (em ambos os cenários), o backend deve registrar no PostgreSQL: nome do arquivo, tamanho, tipo MIME, hash SHA-256 esperado (calculado pelo frontend), usuário, data de início e status **PENDENTE**.
- **RF02.2**: O frontend deve calcular o hash SHA-256 do arquivo completo antes de iniciar o upload e enviá-lo como metadado.
- **RF02.3**: Ao completar o upload com sucesso, o backend deve publicar um evento `upload.completed` no RabbitMQ contendo o ID do registro, a chave do arquivo no storage e o checksum.
- **RF02.4**: Um consumer RabbitMQ deve processar o evento `upload.completed`, validar a integridade (comparar SHA-256 do storage com o esperado) e atualizar o status para **CONCLUÍDO**.
- **RF02.5**: Se a validação de integridade falhar (SHA-256 divergente), o status deve ser atualizado para **CORROMPIDO** e o arquivo deve ser sinalizado para investigação.
- **RF02.6**: O evento deve ser publicado com confirmação do broker (publisher confirms) para garantir que a mensagem chegou ao RabbitMQ.
- **RF02.7**: O consumer deve usar acknowledgment manual — só confirma (ack) após persistir a atualização no PostgreSQL com sucesso.
- **RF02.8**: Mensagens que falharem no processamento devem ir para uma **dead-letter queue** (DLQ) para reprocessamento manual ou automático.

### F03 — Upload via Protocolo TUS (Cenário B)

Upload resumable de arquivos grandes usando o protocolo TUS 1.0. O frontend fatia o arquivo em chunks e os envia sequencialmente ao backend, que grava em disco. Suporta pausa, retomada automática e cancelamento.

- **RF03.1**: O frontend deve fatiar o arquivo em chunks de 100 MB e enviá-los sequencialmente ao backend via protocolo TUS.
- **RF03.2**: O sistema deve exibir barra de progresso atualizada em tempo real com percentual de conclusão.
- **RF03.3**: O usuário deve poder pausar o upload e retomá-lo do ponto exato onde parou, sem retransmitir dados já enviados.
- **RF03.4**: Em caso de queda de rede, o sistema deve retomar automaticamente o upload após reconexão (retry com backoff progressivo).
- **RF03.5**: O usuário deve poder cancelar o upload a qualquer momento, e o arquivo incompleto não deve aparecer na listagem de concluídos.
- **RF03.6**: O sistema deve suportar arquivos de até 250 GB (limite configurável no backend até 300 GB).
- **RF03.7**: Ao cancelar, o registro no PostgreSQL deve ser atualizado para status **CANCELADO** e o arquivo parcial no disco deve ser removido.

### F04 — Upload via MinIO Multipart (Cenário A)

Upload paralelo de arquivos grandes usando pre-signed URLs do MinIO. O backend apenas orquestra (gera URLs e completa o multipart) — os bytes vão direto do browser para o MinIO, sem passar pelo backend.

- **RF04.1**: O frontend deve solicitar ao backend o início de um multipart upload, recebendo um `uploadId` e uma lista de pre-signed URLs (uma por chunk de 100 MB).
- **RF04.2**: O frontend deve enviar os chunks diretamente para o MinIO via `PUT` nas pre-signed URLs, com até 5 uploads em paralelo.
- **RF04.3**: O sistema deve exibir barra de progresso atualizada em tempo real com percentual de conclusão.
- **RF04.4**: Após envio de todos os chunks, o frontend deve notificar o backend com a lista de ETags para consolidação do arquivo no MinIO.
- **RF04.5**: O usuário deve poder cancelar o upload, e o backend deve chamar `AbortMultipartUpload` para limpar os chunks orphaned no MinIO.
- **RF04.6**: Pre-signed URLs devem ter validade de 24 horas.
- **RF04.7**: O sistema deve suportar arquivos de até 250 GB.
- **RF04.8**: Ao cancelar, o registro no PostgreSQL deve ser atualizado para status **CANCELADO** e o `AbortMultipartUpload` deve ser chamado.

### F05 — Listagem e Download de Arquivos

Interface para visualizar arquivos já enviados e fazer download. A listagem é baseada nos registros do PostgreSQL (não no storage diretamente), garantindo que apenas arquivos rastreados sejam exibidos.

- **RF05.1**: O sistema deve listar todos os arquivos com status, nome, tamanho, data de upload e checksum SHA-256.
- **RF05.2**: A listagem deve mostrar o status de cada arquivo (PENDENTE, CONCLUÍDO, CORROMPIDO, CANCELADO).
- **RF05.3**: Apenas arquivos com status CONCLUÍDO devem ter a opção de download habilitada.
- **RF05.4**: No cenário TUS, o download deve ser servido diretamente pelo backend com suporte a Range Requests (`enableRangeProcessing`).
- **RF05.5**: No cenário MinIO, o download deve ser via redirect para pre-signed URL do MinIO (backend não toca nos bytes).
- **RF05.6**: O download deve preservar o nome original do arquivo.

### F06 — Detecção e Limpeza de Dados Órfãos

Mecanismo para garantir que nenhum arquivo permaneça no storage sem registro no banco, e nenhum registro fique em status PENDENTE indefinidamente. Cada arquivo vale $1M — dados órfãos são inaceitáveis.

- **RF06.1**: Uploads com status PENDENTE por mais de **X horas** (configurável, padrão: 24h) devem ser detectados por um job periódico.
- **RF06.2**: O job deve publicar um evento `upload.timeout` na dead-letter queue para cada upload expirado.
- **RF06.3**: O consumer da DLQ deve: (a) tentar verificar se o arquivo existe no storage e está completo → se sim, publicar novo evento de conclusão; (b) se o arquivo não existe ou está incompleto → atualizar status para **FALHA** e remover dados parciais do storage.
- **RF06.4**: No cenário MinIO, lifecycle rules devem expirar uploads multipart incompletos após 3 dias como camada adicional de proteção.
- **RF06.5**: No cenário TUS, arquivos no disco sem registro correspondente no PostgreSQL devem ser detectados e removidos pelo job.
- **RF06.6**: A configuração de lifecycle e jobs deve ser automatizável (script ou startup do backend).
- **RF06.7**: Toda ação de limpeza deve ser logada com detalhes (ID do upload, ação tomada, timestamp) para auditoria.

### F07 — Ambiente de Execução

Infraestrutura para execução da POC em desenvolvimento local e em Kubernetes.

- **RF07.1**: A POC deve ser executável localmente via `docker compose up -d` com todos os serviços (backend, frontend, MinIO, PostgreSQL, RabbitMQ).
- **RF07.2**: O ambiente Kubernetes deve incluir manifests apenas para a **aplicação** (Deployment, PVC, Ingress, Secrets). MinIO e RabbitMQ já existem no cluster.
- **RF07.3**: O Ingress deve estar configurado com `proxy-body-size: 0` e timeouts de 1 hora para suportar uploads longos.
- **RF07.4**: O cenário MinIO deve funcionar com backend 100% stateless (sem volumes compartilhados).
- **RF07.5**: O cenário TUS deve funcionar com 2+ réplicas de backend compartilhando o mesmo PVC (ReadWriteMany via **Longhorn**).
- **RF07.6**: O PostgreSQL no Docker Compose deve ter volume persistente para não perder registros de uploads entre restarts.

---

## Experiência do Usuário

### Personas e Necessidades

| Persona | Necessidade Principal |
|---|---|
| Engenheiro de Plataforma | Executar ambos os cenários, comparar métricas, validar K8s, produzir relatório |
| Usuário Final (simulado) | Upload intuitivo com progresso, controles de pausa/cancel, listagem e download |

### Fluxos Principais

**Fluxo 1 — Login:**
Usuário acessa a aplicação → insere credenciais → recebe token JWT → é redirecionado para a tela de upload.

**Fluxo 2 — Upload TUS:**
Usuário seleciona arquivo → frontend calcula SHA-256 → backend registra metadados no PostgreSQL (status PENDENTE) → upload inicia com barra de progresso → pode pausar/retomar/cancelar → ao completar, backend publica evento no RabbitMQ → consumer valida integridade e atualiza status para CONCLUÍDO → arquivo aparece na listagem como concluído.

**Fluxo 3 — Upload MinIO:**
Usuário seleciona arquivo → frontend calcula SHA-256 → backend registra metadados e gera pre-signed URLs → chunks são enviados em paralelo direto ao MinIO com barra de progresso → pode cancelar → ao completar, backend publica evento no RabbitMQ → consumer valida integridade e atualiza status para CONCLUÍDO → arquivo aparece na listagem como concluído.

**Fluxo 4 — Download:**
Usuário visualiza listagem → vê status de cada arquivo → clica em download (apenas para CONCLUÍDO) → arquivo é baixado (via backend no TUS, via redirect no MinIO).

**Fluxo 5 — Falha / Timeout:**
Upload fica PENDENTE por mais de 24h → job detecta → publica evento na DLQ → consumer verifica storage → se completo, reconcilia; se incompleto, marca como FALHA e limpa storage.

### Considerações de UI/UX

- Interface minimalista focada em funcionalidade (adequada para POC)
- Barra de progresso percentual visível durante todo o upload
- Indicador de cálculo de SHA-256 antes do upload iniciar (pode levar segundos/minutos para arquivos grandes)
- Botões de ação (Pausar, Retomar, Cancelar) habilitados conforme estado do upload
- Mensagens de erro claras em caso de falha
- Tabela de arquivos enviados com **status** (PENDENTE, CONCLUÍDO, CORROMPIDO, CANCELADO), tamanho formatado (KB/MB/GB), data e checksum
- Download habilitado apenas para arquivos com status CONCLUÍDO

### Acessibilidade

- Controles acessíveis via teclado
- Labels semânticos nos campos de input e botões
- Feedback textual do status (não apenas visual)

---

## Restrições Técnicas de Alto Nível

| Categoria | Restrição |
|---|---|
| **Backend** | .NET 8 Web API |
| **Frontend** | React + TypeScript + Vite |
| **Banco de dados** | PostgreSQL (registro de metadados e status dos uploads) |
| **Mensageria** | RabbitMQ com publisher confirms, manual ack e dead-letter queue |
| **Protocolo TUS** | Biblioteca `tusdotnet` no backend, `tus-js-client` no frontend |
| **MinIO** | SDK `AWSSDK.S3` (compatibilidade S3) no backend |
| **Autenticação** | JWT Bearer Token (chave simétrica HMAC-SHA256) |
| **Integridade** | SHA-256 calculado no frontend, validado no backend após upload |
| **Chunk size** | 100 MB por chunk em ambos os cenários |
| **Paralelismo (MinIO)** | 5 chunks simultâneos |
| **Tamanho máximo** | 250 GB (configurável até 300 GB) |
| **Ambiente local** | Docker Compose com todos os serviços (backend, frontend, MinIO, PostgreSQL, RabbitMQ) |
| **Ambiente prod** | Kubernetes on-premise com Ingress Nginx (MinIO e RabbitMQ já existem no cluster) |
| **Storage class K8s** | **Longhorn** para PVCs |
| **Storage TUS** | PVC com `ReadWriteMany` (Longhorn) para multi-pod |
| **Storage MinIO** | MinIO como object storage (já disponível no K8s, container em local) |
| **K8s manifests** | Apenas para a aplicação (Deployment, PVC, Ingress, Secrets) — sem manifests para MinIO/RabbitMQ |
| **Timeouts** | Mínimo 1 hora nos proxies/ingress para suportar uploads longos |
| **Pre-signed URLs** | Validade de 24h para upload, 1h para download |
| **Timeout de órfãos** | Uploads PENDENTE por mais de 24h são tratados automaticamente |

---

## Não-Objetivos (Fora de Escopo)

- **Autenticação de produção**: Não haverá integração com Identity Provider (AD, OAuth2, OIDC). Credenciais são hardcoded para POC.
- **Interface de produção**: A UI é funcional e minimalista. Não há objetivo de design polido, responsividade completa ou tema visual.
- **Multi-tenancy**: Não há isolamento por tenant, organização ou projeto. Todos os uploads vão para o mesmo bucket/diretório.
- **Criptografia end-to-end**: Não há criptografia do arquivo no cliente antes do upload. TLS no transporte é suficiente para a POC.
- **Validação de conteúdo**: Não há verificação de tipo de arquivo, antivírus ou scanning de conteúdo.
- **Notificações ao usuário**: Não há notificações por email ou push ao completar o upload. O RabbitMQ é usado apenas para comunicação interna entre componentes.
- **Versionamento de arquivos**: Não há controle de versão. Uploads com mesmo nome podem sobrescrever.
- **Pause/Resume no MinIO**: O protocolo S3 Multipart não suporta pausa nativa de chunks individuais. Esta funcionalidade fica como evolução futura.
- **Métricas e observabilidade automatizadas**: Coleta de métricas será manual durante a POC (logs e monitoramento visual).
- **Manifests K8s para MinIO/RabbitMQ/PostgreSQL**: Esses serviços já existem no cluster. A POC cria manifests apenas para a aplicação.
- **Cálculo de SHA-256 server-side para arquivos muito grandes**: Se o cálculo no frontend for inviável para 250 GB, pode-se aceitar o checksum apenas para arquivos menores como evolução.

---

## Questões em Aberto

1. **Política de retenção**: Qual o tempo de retenção dos arquivos completos no storage? A POC não define expiração para arquivos finalizados — apenas para uploads incompletos (3 dias).
2. **Limites de concorrência**: Quantos uploads simultâneos de diferentes usuários o sistema deve suportar? A POC valida um upload por vez por cenário.
3. **Rede interna**: Qual a banda de rede disponível entre os nós do cluster e o MinIO? Isso impacta diretamente o tempo de upload e o paralelismo ideal.
4. **SHA-256 para 250 GB no browser**: O cálculo de SHA-256 de um arquivo de 250 GB no frontend pode levar minutos e consumir memória significativa. Avaliar se deve ser feito via Web Worker com streaming ou se existe um limite prático de tamanho para o checksum no frontend.
5. **HTTPS/TLS**: O ambiente de POC usará HTTP. A validação em K8s exige certificados TLS para o Ingress?
6. **RabbitMQ — configuração de DLQ**: Quantas tentativas de retry antes de mover para a DLQ? Sugestão: 3 tentativas com backoff exponencial (1s, 5s, 30s).
