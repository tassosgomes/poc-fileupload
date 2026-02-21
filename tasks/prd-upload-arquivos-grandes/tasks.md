# Resumo de Tarefas de Implementação — POC Upload de Arquivos Grandes (TUS + MinIO)

## Visão Geral

Conjunto de 17 tarefas para implementar a POC que compara duas estratégias de upload de arquivos grandes (até 250 GB): protocolo TUS (upload resumable via backend) e MinIO Multipart Upload (upload direto via pre-signed URLs). A arquitetura segue Clean Architecture (.NET 8), frontend React + Vite + TypeScript, PostgreSQL, RabbitMQ e Docker Compose/Kubernetes.

## Fases de Implementação

### Fase 1 — Fundação (Tarefas 1-3)

Criação da estrutura base do projeto: scaffolding da solution .NET (5 camadas), projeto React, Docker Compose com todos os serviços, camada de domínio e persistência PostgreSQL. Tudo sequencial — cada tarefa depende da anterior.

### Fase 2 — Infraestrutura de Suporte (Tarefas 4-5)

Configuração do RabbitMQ (publisher, consumer, DLQ) e autenticação JWT. Podem ser executadas em **paralelo** após a Fase 1, pois não possuem dependência mútua.

### Fase 3 — Features de Upload Backend (Tarefas 6-9)

Implementação dos endpoints de upload: registro de metadados (base para ambos os cenários), MinIO multipart, TUS resumable e consumer de integridade SHA-256. Sequencial — cada tarefa constrói sobre a anterior.

### Fase 4 — Features Complementares Backend (Tarefas 10-11)

Listagem/download de arquivos e detecção de órfãos. Podem ser executadas em **paralelo** após a Fase 3.

### Fase 5 — Frontend (Tarefas 12-15)

Setup do projeto React + autenticação, depois as três features de UI (TUS, MinIO, listagem) em **paralelo**.

### Fase 6 — Finalização (Tarefas 16-17)

Kubernetes YAMLs e testes unitários. Podem ser executadas em **paralelo**.

## Tarefas

- [x] 1.0 Scaffolding do Projeto e Docker Compose
- [x] 2.0 Camada Domain — Entidades, Enums e Interfaces
- [x] 3.0 Camada Infra — PostgreSQL e EF Core
- [x] 4.0 Infra Layer — RabbitMQ (Publisher, Consumer, DLQ)
- [x] 5.0 Autenticação JWT (F01)
- [x] 6.0 Registro de Metadados e Cancelamento (F02 — Base)
- [ ] 7.0 Upload MinIO — Backend (F04)
- [ ] 8.0 Upload TUS — Backend (F03)
- [ ] 9.0 Consumer de Integridade SHA-256 (F02 — Continuação)
- [ ] 10.0 Listagem e Download de Arquivos (F05)
- [ ] 11.0 Detecção e Limpeza de Dados Órfãos (F06)
- [ ] 12.0 Frontend — Setup, Auth e Componentes Compartilhados
- [ ] 13.0 Frontend — Upload TUS
- [ ] 14.0 Frontend — Upload MinIO
- [ ] 15.0 Frontend — Listagem e Download
- [ ] 16.0 Kubernetes YAMLs (F07)
- [ ] 17.0 Testes Unitários

## Análise de Paralelização

### Lanes de Execução Paralela

| Lane | Tarefas | Descrição |
|------|---------|-----------|
| Lane A (Backend Core) | 1.0 → 2.0 → 3.0 → 6.0 → 7.0 → 8.0 → 9.0 | Caminho crítico: fundação → domínio → persistência → features de upload → consumer |
| Lane B (Infra RabbitMQ) | 4.0 | RabbitMQ publisher/consumer/DLQ — paralelo com 5.0 após 3.0 |
| Lane C (Auth) | 5.0 | JWT auth — paralelo com 4.0 após 3.0 |
| Lane D (Backend Complementar) | 10.0, 11.0 | Listagem/download e órfãos — paralelos após 9.0 |
| Lane E (Frontend) | 12.0 → (13.0 ∥ 14.0 ∥ 15.0) | Setup React → features de UI em paralelo |
| Lane F (Finalização) | 16.0 ∥ 17.0 | K8s YAMLs e testes unitários — paralelos ao final |

### Caminho Crítico

```
1.0 → 2.0 → 3.0 → [4.0 + 5.0] → 6.0 → 7.0 → 8.0 → 9.0 → [10.0 + 11.0] → 17.0
                                                                      ↓
                                          12.0 → [13.0 + 14.0 + 15.0] → 16.0
```

O caminho crítico passa por: **1.0 → 2.0 → 3.0 → 5.0 → 6.0 → 7.0 → 8.0 → 9.0 → 10.0 → 17.0** (10 tarefas sequenciais que determinam o tempo mínimo de conclusão).

### Diagrama de Dependências

```
                    ┌─────┐
                    │ 1.0 │ Scaffolding + Docker Compose
                    └──┬──┘
                       │
                    ┌──▼──┐
                    │ 2.0 │ Domain Layer
                    └──┬──┘
                       │
                    ┌──▼──┐
                    │ 3.0 │ PostgreSQL + EF Core
                    └──┬──┘
                       │
              ┌────────┼───────┐
              │        │       │
           ┌──▼──┐  ┌──▼──┐  ┌─▼──┐
           │ 4.0 │  │ 5.0 │  │12.0│ Frontend Setup
           └──┬──┘  └──┬──┘  └──┬─┘
              │        │        │
              └────┬───┘   ┌────┼────────┐
                   │       │    │        │
                ┌──▼──┐  ┌─▼──┐ │┌──▼──┐ ┌▼───┐
                │ 6.0 │  │13.0│ ││14.0 │ │15.0│
                └──┬──┘  └────┘ │└─────┘ └────┘
                   │            │
                ┌──▼──┐         │
                │ 7.0 │         │
                └──┬──┘         │
                   │            │
                ┌──▼──┐         │
                │ 8.0 │         │
                └──┬──┘         │
                   │            │
                ┌──▼──┐         │
                │ 9.0 │         │
                └──┬──┘         │
                   │            │
              ┌────┼────┐       │
              │         │       │
           ┌──▼──┐   ┌──▼──┐    │
           │10.0 │   │11.0 │    │
           └──┬──┘   └──┬──┘    │
              │         │       │
              └────┬────┘       │
                   │            │
              ┌────┼────────────┘
              │    │
           ┌──▼──┐ │┌──▼──┐
           │16.0 │ ││17.0 │
           └─────┘ │└─────┘
```
