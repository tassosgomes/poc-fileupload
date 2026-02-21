---
status: pending
parallelizable: false
blocked_by: []
---

<task_context>
<domain>infra/devops</domain>
<type>implementation</type>
<scope>configuration</scope>
<complexity>medium</complexity>
<dependencies>docker</dependencies>
<unblocks>"2.0", "12.0"</unblocks>
</task_context>

# Tarefa 1.0: Scaffolding do Projeto e Docker Compose

## Visão Geral

Criar a estrutura completa do projeto seguindo Clean Architecture com 5 camadas no backend (.NET 8) e feature-based no frontend (React + Vite + TypeScript). Inclui o `docker-compose.yml` com todos os serviços necessários (PostgreSQL, RabbitMQ, MinIO, backend, frontend) e os Dockerfiles.

Esta é a tarefa fundacional — sem ela, nenhuma outra tarefa pode ser desenvolvida ou testada localmente.

## Requisitos

- RF07.1: A POC deve ser executável localmente via `docker compose up -d` com todos os serviços.
- RF07.6: O PostgreSQL no Docker Compose deve ter volume persistente.
- A solution .NET 8 deve seguir a estrutura de 5 camadas definida na techspec.
- O projeto React deve ser criado com Vite + TypeScript.

## Subtarefas

- [ ] 1.1 Criar solution .NET 8 (`UploadPoc.sln`) com 5 projetos:
  - `1-Services/UploadPoc.API` (Web API)
  - `2-Application/UploadPoc.Application` (Class Library)
  - `3-Domain/UploadPoc.Domain` (Class Library)
  - `4-Infra/UploadPoc.Infra` (Class Library)
  - `5-Tests/UploadPoc.UnitTests` (xUnit Test Project)
- [ ] 1.2 Configurar referências entre projetos (API → Application → Domain ← Infra)
- [ ] 1.3 Adicionar pacotes NuGet conforme techspec em cada projeto
- [ ] 1.4 Criar `Program.cs` mínimo (Hello World + Swagger) no projeto API
- [ ] 1.5 Criar Dockerfile do backend (`backend/Dockerfile`) — multi-stage build (.NET 8 SDK → ASP.NET 8 runtime)
- [ ] 1.6 Criar projeto React com Vite (`npm create vite@latest frontend -- --template react-ts`)
- [ ] 1.7 Instalar dependências npm conforme techspec (`react-router-dom`, `tus-js-client`, `axios`)
- [ ] 1.8 Criar Dockerfile do frontend (`frontend/Dockerfile`) — multi-stage build (Node → Nginx)
- [ ] 1.9 Criar `frontend/nginx.conf` com proxy reverso para o backend
- [ ] 1.10 Criar `docker-compose.yml` na raiz com todos os serviços:
  - `postgresql` (postgres:16-alpine, porta 5432, volume persistente)
  - `rabbitmq` (rabbitmq:3.13-management-alpine, portas 5672/15672)
  - `minio` (minio/minio:latest, portas 9000/9001, volume persistente)
  - `minio-setup` (minio/mc:latest, cria bucket `uploads`)
  - `backend` (build ./backend, porta 5000→8080, env vars)
  - `frontend` (build ./frontend, porta 3000→80)
- [ ] 1.11 Adicionar `appsettings.json` com seções de configuração (JWT, MinIO, RabbitMQ, TusStorage)
- [ ] 1.12 Validar que `docker compose up -d` sobe todos os serviços sem erro

## Sequenciamento

- Bloqueado por: Nenhum (primeira tarefa)
- Desbloqueia: 2.0 (Domain), 12.0 (Frontend Setup)
- Paralelizável: Não (é a fundação de tudo)

## Detalhes de Implementação

### Estrutura de Pastas do Backend

```
backend/
├── UploadPoc.sln
├── Dockerfile
├── 1-Services/
│   └── UploadPoc.API/
│       ├── UploadPoc.API.csproj
│       ├── Program.cs
│       └── appsettings.json
├── 2-Application/
│   └── UploadPoc.Application/
│       └── UploadPoc.Application.csproj
├── 3-Domain/
│   └── UploadPoc.Domain/
│       └── UploadPoc.Domain.csproj
├── 4-Infra/
│   └── UploadPoc.Infra/
│       └── UploadPoc.Infra.csproj
└── 5-Tests/
    └── UploadPoc.UnitTests/
        └── UploadPoc.UnitTests.csproj
```

### Referências entre Projetos

```
API → Application, Infra
Application → Domain
Infra → Domain
UnitTests → Application, Domain, Infra
```

### Pacotes NuGet por Projeto

**UploadPoc.API:**
```xml
<PackageReference Include="tusdotnet" Version="2.8.1" />
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.0" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="6.5.0" />
<PackageReference Include="Serilog.AspNetCore" Version="8.0.0" />
<PackageReference Include="Serilog.Sinks.Console" Version="5.0.1" />
<PackageReference Include="Serilog.Formatting.Compact" Version="2.0.0" />
```

**UploadPoc.Application:**
```xml
<PackageReference Include="FluentValidation" Version="11.8.1" />
<PackageReference Include="FluentValidation.AspNetCore" Version="11.3.0" />
```

**UploadPoc.Infra:**
```xml
<PackageReference Include="AWSSDK.S3" Version="3.7.305" />
<PackageReference Include="RabbitMQ.Client" Version="6.8.1" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="8.0.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.0" />
<PackageReference Include="Polly" Version="8.2.0" />
<PackageReference Include="AspNetCore.HealthChecks.NpgSql" Version="7.1.0" />
<PackageReference Include="AspNetCore.HealthChecks.Rabbitmq" Version="7.1.0" />
```

**UploadPoc.UnitTests:**
```xml
<PackageReference Include="xunit" Version="2.6.6" />
<PackageReference Include="Moq" Version="4.20.70" />
<PackageReference Include="AwesomeAssertions" Version="6.15.1" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
```

### Docker Compose

Usar exatamente a configuração definida na techspec (seção "Docker Compose"), com:
- Credenciais fixas para POC (PostgreSQL: poc/poc123, RabbitMQ: poc/poc123, MinIO: minioadmin/minioadmin123)
- Volumes nomeados: `pg_data`, `minio_data`, `tus_data`
- Health check no MinIO para garantir que `minio-setup` só roda após o MinIO estar pronto
- Backend exposto em porta 5000, frontend em porta 3000

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=uploadpoc;Username=poc;Password=poc123"
  },
  "Jwt": {
    "Secret": "poc-jwt-secret-minimo-32-caracteres-aqui!!",
    "Issuer": "upload-poc",
    "Audience": "upload-poc",
    "ExpirationHours": 8
  },
  "MinIO": {
    "Endpoint": "localhost:9000",
    "AccessKey": "minioadmin",
    "SecretKey": "minioadmin123",
    "BucketName": "uploads",
    "UseSSL": false
  },
  "RabbitMQ": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "poc",
    "Password": "poc123"
  },
  "TusStorage": {
    "Path": "/app/uploads"
  },
  "OrphanCleanup": {
    "TimeoutHours": 24,
    "IntervalMinutes": 60
  }
}
```

### Nginx Config (frontend)

```nginx
server {
    listen 80;
    root /usr/share/nginx/html;
    index index.html;

    location /api/ {
        proxy_pass http://backend:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        client_max_body_size 0;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location /upload/tus {
        proxy_pass http://backend:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        client_max_body_size 0;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location / {
        try_files $uri $uri/ /index.html;
    }
}
```

## Critérios de Sucesso

- `dotnet build` compila a solution sem erros
- `docker compose up -d` sobe todos os 6 serviços (postgresql, rabbitmq, minio, minio-setup, backend, frontend)
- Backend responde em `http://localhost:5000/swagger`
- Frontend responde em `http://localhost:3000`
- PostgreSQL acessível em `localhost:5432`
- RabbitMQ Management UI acessível em `http://localhost:15672`
- MinIO Console acessível em `http://localhost:9001`
- Bucket `uploads` criado automaticamente no MinIO
