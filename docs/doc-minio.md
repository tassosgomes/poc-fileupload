# POC — Upload de Arquivos Grandes: Cenário MinIO

> **Stack:** .NET 8 · React + Vite · MinIO · Docker Compose · JWT  
> **Estratégia:** Multipart Upload com Pre-signed URLs — o backend **não toca nos bytes**, apenas orquestra.

---

## Índice

1. [Visão Geral da Arquitetura](#1-visão-geral-da-arquitetura)
2. [Docker Compose — Ambiente Local Completo](#2-docker-compose--ambiente-local-completo)
3. [Backend .NET 8](#3-backend-net-8)
   - [Dependências NuGet](#31-dependências-nuget)
   - [Program.cs](#32-programcs)
   - [MinioController.cs](#33-miniocontrollercs)
   - [FilesController.cs (listagem + download)](#34-filescontrollercs--listagem--download)
   - [appsettings.json](#35-appsettingsjson)
4. [Frontend React](#4-frontend-react)
   - [useMinioUpload.ts](#41-useminiouploadts)
   - [MinioUploadPage.tsx](#42-miniouploadpagetsx)
5. [Lifecycle Rules — Limpeza de Uploads Incompletos](#5-lifecycle-rules--limpeza-de-uploads-incompletos)
6. [Kubernetes — Manifests (Produção)](#6-kubernetes--manifests-produção)
7. [Comparativo TUS vs MinIO](#7-comparativo-tus-vs-minio)
8. [Checklist de Validação](#8-checklist-de-validação)

---

## 1. Visão Geral da Arquitetura

```
┌─────────────┐   1. POST /upload/minio/initiate    ┌─────────────────┐
│             │ ─────────────────────────────────►  │                 │
│   React     │                                     │  Backend .NET   │
│  Frontend   │ ◄─────────────────────────────────  │  (Orquestrador) │
│             │   2. { uploadId, presignedUrls[] }  │                 │
└─────────────┘                                     └───────┬─────────┘
       │                                                    │
       │  3. PUT chunk 1..N direto para o MinIO             │ SDK MinIO
       │     (5 chunks em paralelo, sem passar              │ (gera URLs,
       │      pelo backend)                                 │  completa MPU)
       ▼                                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                          MinIO                                      │
│              (S3-compatible object storage)                         │
└─────────────────────────────────────────────────────────────────────┘
       │
       │  4. POST /upload/minio/complete  ──►  Backend chama CompleteMultipartUpload
```

### Por que o backend não toca nos bytes?

No cenário TUS, cada chunk passa pelo backend antes de chegar ao disco. Em um arquivo de 250 GB com chunks de 100 MB, isso são 2.500 requisições que consomem CPU e RAM do backend.

No cenário MinIO, o React faz `PUT` diretamente nas URLs pré-assinadas. O backend só é chamado duas vezes: no início (para gerar as URLs) e no fim (para completar o multipart). O tráfego pesado de rede vai direto do cliente para o MinIO.

---

## 2. Docker Compose — Ambiente Local Completo

O compose sobe **todos os serviços da POC**: MinIO, backend .NET e frontend React.

```yaml
# docker-compose.yml
version: "3.9"

services:

  # ── MinIO ────────────────────────────────────────────────────────────────
  minio:
    image: minio/minio:latest
    container_name: poc-minio
    command: server /data --console-address ":9001"
    environment:
      MINIO_ROOT_USER: minioadmin
      MINIO_ROOT_PASSWORD: minioadmin123
    ports:
      - "9000:9000"   # API S3
      - "9001:9001"   # Console web (http://localhost:9001)
    volumes:
      - minio_data:/data
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:9000/minio/health/live"]
      interval: 10s
      timeout: 5s
      retries: 5

  # ── Cria o bucket automaticamente na primeira subida ─────────────────────
  minio-setup:
    image: minio/mc:latest
    container_name: poc-minio-setup
    depends_on:
      minio:
        condition: service_healthy
    entrypoint: >
      /bin/sh -c "
        mc alias set local http://minio:9000 minioadmin minioadmin123;
        mc mb --ignore-existing local/uploads;
        mc anonymous set none local/uploads;
        echo 'Bucket criado com sucesso';
        exit 0;
      "

  # ── Backend .NET 8 ────────────────────────────────────────────────────────
  backend:
    build:
      context: ./backend
      dockerfile: Dockerfile
    container_name: poc-backend
    depends_on:
      minio:
        condition: service_healthy
    environment:
      ASPNETCORE_URLS: "http://+:8080"
      Jwt__Secret: "poc-jwt-secret-minimo-32-caracteres-aqui"
      MinIO__Endpoint: "minio:9000"
      MinIO__AccessKey: "minioadmin"
      MinIO__SecretKey: "minioadmin123"
      MinIO__BucketName: "uploads"
      MinIO__UseSSL: "false"
    ports:
      - "5000:8080"

  # ── Frontend React ────────────────────────────────────────────────────────
  frontend:
    build:
      context: ./frontend
      dockerfile: Dockerfile
    container_name: poc-frontend
    depends_on:
      - backend
    ports:
      - "3000:80"

volumes:
  minio_data:
```

### Dockerfile — Backend

```dockerfile
# backend/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "UploadPoc.dll"]
```

### Dockerfile — Frontend

```dockerfile
# frontend/Dockerfile
FROM node:20-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
```

### nginx.conf (frontend)

```nginx
# frontend/nginx.conf
server {
  listen 80;

  location / {
    root   /usr/share/nginx/html;
    index  index.html;
    try_files $uri $uri/ /index.html;  # SPA routing
  }

  # Proxy para o backend — evita CORS em produção
  location /api/ {
    proxy_pass http://backend:8080/;
    proxy_set_header Host $host;
    proxy_read_timeout 3600;
    proxy_send_timeout 3600;
    client_max_body_size 0;
  }
}
```

### Comandos para subir o ambiente

```bash
# Subir tudo
docker compose up -d

# Acompanhar logs
docker compose logs -f backend

# Acessar console do MinIO
# http://localhost:9001  (usuário: minioadmin / senha: minioadmin123)

# Derrubar e limpar volumes
docker compose down -v
```

---

## 3. Backend .NET 8

### 3.1 Dependências NuGet

```bash
dotnet add package AWSSDK.S3
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add package System.IdentityModel.Tokens.Jwt
```

> O SDK da AWS é usado porque o MinIO é 100% compatível com a API S3. Não existe um SDK oficial do MinIO para .NET — a abordagem recomendada pelo próprio MinIO é usar o `AWSSDK.S3` apontando para o endpoint do MinIO.

### 3.2 Program.cs

```csharp
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ── JWT ──────────────────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!))
        };
    });

builder.Services.AddAuthorization();

// ── SDK S3/MinIO ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var cfg = builder.Configuration;
    var s3Config = new AmazonS3Config
    {
        ServiceURL        = $"http://{cfg["MinIO:Endpoint"]}",
        ForcePathStyle    = true,   // OBRIGATÓRIO para MinIO
        UseHttp           = true
    };
    return new AmazonS3Client(
        cfg["MinIO:AccessKey"],
        cfg["MinIO:SecretKey"],
        s3Config);
});

builder.Services.AddControllers();
builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
```

### 3.3 MinioController.cs

O controller tem três responsabilidades: **iniciar** o multipart upload (gera as pre-signed URLs), **completar** (junta os chunks) e **abortar** (limpa uploads cancelados).

```csharp
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("upload/minio")]
[Authorize]
public class MinioController : ControllerBase
{
    private readonly IAmazonS3 _s3;
    private readonly IConfiguration _config;
    private const int ChunkSizeBytes = 100 * 1024 * 1024; // 100 MB

    public MinioController(IAmazonS3 s3, IConfiguration config)
    {
        _s3 = s3;
        _config = config;
    }

    // ── 1. Inicia o multipart upload e devolve as pre-signed URLs ──────────
    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate([FromBody] InitiateRequest req)
    {
        var bucket = _config["MinIO:BucketName"]!;

        // Inicia o multipart upload no MinIO
        var initiateResponse = await _s3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                BucketName  = bucket,
                Key         = req.FileName,
                ContentType = req.ContentType ?? "application/octet-stream"
            });

        var uploadId = initiateResponse.UploadId;
        var totalChunks = (int)Math.Ceiling((double)req.FileSizeBytes / ChunkSizeBytes);

        // Gera uma pre-signed URL por chunk (válidas por 24h)
        var urls = Enumerable.Range(1, totalChunks).Select(partNumber =>
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key        = req.FileName,
                Verb       = HttpVerb.PUT,
                Expires    = DateTime.UtcNow.AddHours(24),
                UploadId   = uploadId,
                PartNumber = partNumber
            };
            return new
            {
                partNumber,
                url = _s3.GetPreSignedURL(request)
            };
        }).ToList();

        return Ok(new
        {
            uploadId,
            key      = req.FileName,
            parts    = urls,
            chunkSize = ChunkSizeBytes
        });
    }

    // ── 2. Completa o multipart — MinIO consolida os chunks em um arquivo ──
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteRequest req)
    {
        var bucket = _config["MinIO:BucketName"]!;

        await _s3.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key        = req.Key,
                UploadId   = req.UploadId,
                PartETags  = req.Parts
                    .Select(p => new PartETag(p.PartNumber, p.ETag))
                    .ToList()
            });

        return Ok(new { message = "Upload concluído", key = req.Key });
    }

    // ── 3. Aborta — limpa chunks de uploads cancelados ─────────────────────
    [HttpDelete("abort")]
    public async Task<IActionResult> Abort([FromBody] AbortRequest req)
    {
        await _s3.AbortMultipartUploadAsync(
            new AbortMultipartUploadRequest
            {
                BucketName = _config["MinIO:BucketName"]!,
                Key        = req.Key,
                UploadId   = req.UploadId
            });

        return Ok(new { message = "Upload abortado" });
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────
public record InitiateRequest(string FileName, long FileSizeBytes, string? ContentType);
public record CompleteRequest(string Key, string UploadId, List<PartInfo> Parts);
public record AbortRequest(string Key, string UploadId);
public record PartInfo(int PartNumber, string ETag);
```

### 3.4 FilesController.cs — Listagem + Download

```csharp
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("files")]
[Authorize]
public class FilesController : ControllerBase
{
    private readonly IAmazonS3 _s3;
    private readonly IConfiguration _config;

    public FilesController(IAmazonS3 s3, IConfiguration config)
    {
        _s3 = s3;
        _config = config;
    }

    // ── Listagem ──────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var response = await _s3.ListObjectsV2Async(
            new ListObjectsV2Request
            {
                BucketName = _config["MinIO:BucketName"]!
            });

        var files = response.S3Objects.Select(o => new
        {
            key          = o.Key,
            sizeBytes    = o.Size,
            lastModified = o.LastModified
        });

        return Ok(files);
    }

    // ── Download via pre-signed URL (válida por 1h) ───────────────────────
    [HttpGet("{key}/download")]
    public IActionResult Download(string key)
    {
        var url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName  = _config["MinIO:BucketName"]!,
            Key         = key,
            Verb        = HttpVerb.GET,
            Expires     = DateTime.UtcNow.AddHours(1)
        });

        // Redireciona o browser direto para o MinIO — o backend não toca nos bytes
        return Redirect(url);
    }
}
```

> **Por que redirecionar?** O download de 250 GB passando pelo backend consumiria toda a banda e memória do pod. O redirect faz o browser baixar direto do MinIO, mantendo o backend fora do caminho de dados.

### 3.5 appsettings.json

```json
{
  "Jwt": {
    "Secret": "poc-jwt-secret-minimo-32-caracteres-aqui"
  },
  "MinIO": {
    "Endpoint":   "localhost:9000",
    "AccessKey":  "minioadmin",
    "SecretKey":  "minioadmin123",
    "BucketName": "uploads",
    "UseSSL":     false
  }
}
```

---

## 4. Frontend React

### 4.1 useMinioUpload.ts

O hook gerencia todo o ciclo do upload paralelo: divide o arquivo em chunks, faz 5 uploads simultâneos, coleta os ETags retornados pelo MinIO e chama o endpoint de complete.

```typescript
import { useRef, useState, useCallback } from 'react';
import axios from 'axios';

// ── Tipos ──────────────────────────────────────────────────────────────────
interface PartInfo {
  partNumber: number;
  etag: string;
}

interface UploadState {
  progress: number;
  status: 'idle' | 'uploading' | 'paused' | 'done' | 'error';
  error: string | null;
}

// ── Constantes ─────────────────────────────────────────────────────────────
const PARALLEL_CHUNKS = 5;
const BACKEND_URL = 'http://localhost:5000';

// ── Hook ───────────────────────────────────────────────────────────────────
export function useMinioUpload(token: string) {
  const [state, setState] = useState<UploadState>({
    progress: 0,
    status: 'idle',
    error: null,
  });

  // Controle de cancelamento por AbortController
  const abortControllersRef = useRef<AbortController[]>([]);
  const uploadContextRef = useRef<{
    key: string;
    uploadId: string;
  } | null>(null);

  const api = axios.create({
    baseURL: BACKEND_URL,
    headers: { Authorization: `Bearer ${token}` },
  });

  // ── Upload de um único chunk diretamente para o MinIO ──────────────────
  const uploadChunk = async (
    url: string,
    chunk: Blob,
    partNumber: number,
    onChunkDone: () => void,
    signal: AbortSignal
  ): Promise<PartInfo> => {
    const response = await fetch(url, {
      method: 'PUT',
      body: chunk,
      signal,
      // Sem Authorization aqui — a URL pré-assinada já contém as credenciais
    });

    if (!response.ok) {
      throw new Error(`Chunk ${partNumber} falhou: ${response.status}`);
    }

    const etag = response.headers.get('ETag') ?? '';
    onChunkDone();

    return { partNumber, etag: etag.replace(/"/g, '') };
  };

  // ── Executa N promises em grupos de `concurrency` ──────────────────────
  async function runWithConcurrency<T>(
    tasks: (() => Promise<T>)[],
    concurrency: number
  ): Promise<T[]> {
    const results: T[] = new Array(tasks.length);
    let index = 0;

    const worker = async () => {
      while (index < tasks.length) {
        const i = index++;
        results[i] = await tasks[i]();
      }
    };

    // Sobe `concurrency` workers em paralelo
    await Promise.all(Array.from({ length: concurrency }, worker));
    return results;
  }

  // ── Start ──────────────────────────────────────────────────────────────
  const start = useCallback(async (file: File) => {
    setState({ progress: 0, status: 'uploading', error: null });
    abortControllersRef.current = [];

    try {
      // 1. Inicia o multipart no backend — recebe uploadId e pre-signed URLs
      const { data } = await api.post('/upload/minio/initiate', {
        fileName:      file.name,
        fileSizeBytes: file.size,
        contentType:   file.type || 'application/octet-stream',
      });

      const { uploadId, key, parts, chunkSize } = data;
      uploadContextRef.current = { key, uploadId };

      let completedChunks = 0;
      const totalChunks = parts.length;

      // 2. Monta a lista de tasks — uma por chunk
      const tasks = parts.map(
        (part: { partNumber: number; url: string }) =>
          () => {
            const start = (part.partNumber - 1) * chunkSize;
            const end   = Math.min(start + chunkSize, file.size);
            const chunk = file.slice(start, end);

            const controller = new AbortController();
            abortControllersRef.current.push(controller);

            return uploadChunk(
              part.url,
              chunk,
              part.partNumber,
              () => {
                completedChunks++;
                setState(s => ({
                  ...s,
                  progress: Math.round((completedChunks / totalChunks) * 100),
                }));
              },
              controller.signal
            );
          }
      );

      // 3. Executa com 5 chunks em paralelo
      const uploadedParts = await runWithConcurrency(tasks, PARALLEL_CHUNKS);

      // 4. Completa o multipart — MinIO consolida os chunks
      await api.post('/upload/minio/complete', {
        key,
        uploadId,
        parts: uploadedParts.map(p => ({
          partNumber: p.partNumber,
          eTag:       p.etag,
        })),
      });

      setState({ progress: 100, status: 'done', error: null });
      uploadContextRef.current = null;

    } catch (err: any) {
      if (err.name === 'AbortError') return; // cancelamento intencional
      setState(s => ({ ...s, status: 'error', error: err.message }));
    }
  }, [token]);

  // ── Cancel ─────────────────────────────────────────────────────────────
  const cancel = useCallback(async () => {
    // Aborta todos os fetches em andamento
    abortControllersRef.current.forEach(c => c.abort());
    abortControllersRef.current = [];

    // Limpa o multipart no MinIO para não deixar lixo no storage
    if (uploadContextRef.current) {
      try {
        await api.delete('/upload/minio/abort', {
          data: uploadContextRef.current,
        });
      } catch { /* best-effort */ }
      uploadContextRef.current = null;
    }

    setState({ progress: 0, status: 'idle', error: null });
  }, [token]);

  return { ...state, start, cancel };
}
```

> **Por que não tem `pause/resume` neste hook?** O protocolo S3 Multipart não suporta pausar um chunk no meio. A pausa real exigiria salvar quais chunks já foram completados (via `localStorage` ou backend) e retomar apenas os pendentes. Para a POC, o foco é validar o paralelismo e o cancel limpo. A retomada pode ser adicionada como evolução.

### 4.2 MinioUploadPage.tsx

```tsx
import { useState, useEffect } from 'react';
import axios from 'axios';
import { useMinioUpload } from '../hooks/useMinioUpload';

const BACKEND_URL = 'http://localhost:5000';

export function MinioUploadPage() {
  const token = localStorage.getItem('token') ?? '';
  const { progress, status, error, start, cancel } = useMinioUpload(token);
  const [files, setFiles] = useState<any[]>([]);

  const api = axios.create({
    baseURL: BACKEND_URL,
    headers: { Authorization: `Bearer ${token}` },
  });

  const loadFiles = () =>
    api.get('/files').then(r => setFiles(r.data));

  useEffect(() => {
    loadFiles();
  }, [status]);

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) start(file);
  };

  const download = (key: string) => {
    // Backend redireciona para pre-signed URL do MinIO
    window.open(`${BACKEND_URL}/files/${encodeURIComponent(key)}/download?token=${token}`);
  };

  const formatSize = (bytes: number) => {
    if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
    if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
    return `${(bytes / 1024).toFixed(0)} KB`;
  };

  return (
    <div style={{ padding: 32, maxWidth: 860, margin: '0 auto' }}>
      <h1>Upload MinIO — Multipart (5 chunks paralelos)</h1>

      {/* Seleção de arquivo */}
      <div style={{ marginBottom: 24 }}>
        <input
          type="file"
          onChange={handleFileChange}
          disabled={status === 'uploading'}
        />
      </div>

      {/* Progresso */}
      {status !== 'idle' && (
        <div style={{ marginBottom: 32 }}>
          <progress
            value={progress}
            max={100}
            style={{ width: '100%', height: 20 }}
          />
          <p>
            <strong>{progress}%</strong> —{' '}
            {status === 'uploading' && '📤 Enviando...'}
            {status === 'done'      && '✅ Concluído!'}
            {status === 'error'     && `❌ Erro: ${error}`}
          </p>
          {status === 'uploading' && (
            <button onClick={cancel} style={{ color: 'red' }}>
              Cancelar upload
            </button>
          )}
        </div>
      )}

      {/* Listagem */}
      <h2>Arquivos no MinIO</h2>
      {files.length === 0 ? (
        <p>Nenhum arquivo enviado ainda.</p>
      ) : (
        <table
          border={1}
          cellPadding={10}
          style={{ width: '100%', borderCollapse: 'collapse' }}
        >
          <thead style={{ background: '#f3f4f6' }}>
            <tr>
              <th align="left">Nome</th>
              <th align="right">Tamanho</th>
              <th align="left">Modificado em</th>
              <th>Ação</th>
            </tr>
          </thead>
          <tbody>
            {files.map(f => (
              <tr key={f.key}>
                <td>{f.key}</td>
                <td align="right">{formatSize(f.sizeBytes)}</td>
                <td>{new Date(f.lastModified).toLocaleString('pt-BR')}</td>
                <td align="center">
                  <button onClick={() => download(f.key)}>⬇ Download</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
```

---

## 5. Lifecycle Rules — Limpeza de Uploads Incompletos

Quando um usuário cancela um upload de 250 GB na metade, os chunks já enviados ficam "orphaned" no MinIO — consumindo espaço sem nunca serem consolidados.

### Via Console MinIO (manual — para a POC)

1. Acesse `http://localhost:9001`
2. Vá em **Buckets → uploads → Lifecycle**
3. Adicione uma regra:
   - **Prefix:** (vazio — aplica a todo o bucket)
   - **Expire incomplete multipart uploads after:** `3 days`

### Via mc CLI (automatizável)

```bash
# Configurar alias local
mc alias set local http://localhost:9000 minioadmin minioadmin123

# Aplicar lifecycle rule no bucket uploads
mc ilm rule add \
  --expire-days 3 \
  --noncurrent-expire-days 1 \
  --prefix "" \
  local/uploads

# Verificar regras aplicadas
mc ilm rule ls local/uploads
```

### Via API S3 (automatizar no startup do backend)

```csharp
// Pode ser chamado uma vez no startup do Program.cs
private static async Task EnsureLifecycleRuleAsync(IAmazonS3 s3, string bucket)
{
    await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
    {
        BucketName    = bucket,
        Configuration = new LifecycleConfiguration
        {
            Rules =
            [
                new LifecycleRule
                {
                    Id     = "cleanup-incomplete-multipart",
                    Status = LifecycleRuleStatus.Enabled,
                    AbortIncompleteMultipartUpload =
                        new LifecycleRuleAbortIncompleteMultipartUpload
                        {
                            DaysAfterInitiation = 3 // expurga em 3 dias
                        }
                }
            ]
        }
    });
}
```

---

## 6. Kubernetes — Manifests (Produção)

Para produção, o MinIO roda no próprio cluster via Helm e o backend não precisa mais de PVC.

### MinIO via Helm

```bash
helm repo add minio https://charts.min.io/
helm install minio minio/minio \
  --set rootUser=minioadmin \
  --set rootPassword=minioadmin123 \
  --set persistence.size=5Ti \
  --set replicas=4 \
  --namespace storage \
  --create-namespace
```

### Deployment do backend (sem volume — MinIO cuida do storage)

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: upload-backend
spec:
  replicas: 3   # Pode escalar livremente — sem estado no backend
  selector:
    matchLabels:
      app: upload-backend
  template:
    metadata:
      labels:
        app: upload-backend
    spec:
      containers:
        - name: backend
          image: seu-registry/upload-backend:latest
          ports:
            - containerPort: 8080
          env:
            - name: MinIO__Endpoint
              value: "minio.storage.svc.cluster.local:9000"
            - name: MinIO__AccessKey
              valueFrom:
                secretKeyRef:
                  name: upload-secrets
                  key: minio-access-key
            - name: MinIO__SecretKey
              valueFrom:
                secretKeyRef:
                  name: upload-secrets
                  key: minio-secret-key
            - name: MinIO__BucketName
              value: "uploads"
            - name: Jwt__Secret
              valueFrom:
                secretKeyRef:
                  name: upload-secrets
                  key: jwt-secret
```

> **Diferença crítica em relação ao cenário TUS:** no cenário TUS o backend precisa de PVC com `ReadWriteMany`. No cenário MinIO o backend é 100% stateless — pode ter quantas réplicas quiser sem se preocupar com volume compartilhado.

---

## 7. Comparativo TUS vs MinIO

| Critério                         | TUS (tusdotnet)              | MinIO (Multipart S3)              |
|----------------------------------|------------------------------|-----------------------------------|
| Bytes passam pelo backend?       | ✅ Sim                       | ❌ Não (direto client → MinIO)    |
| Retomada após falha de rede      | ✅ Nativa (protocolo TUS)    | ⚠️ Manual (salvar estado localmente) |
| Backend stateless no K8s?        | ❌ Precisa de PVC RWX        | ✅ Sem volume compartilhado       |
| Escalabilidade do backend        | Limitada pelo volume         | Horizontal livre                  |
| Infraestrutura extra necessária  | Apenas PVC/NFS               | MinIO rodando                     |
| Complexidade de implementação    | Baixa (tusdotnet abstrai)    | Média (controle de ETags)         |
| Cenário recomendado              | Sem object storage           | Com object storage disponível     |

---

## 8. Checklist de Validação

### Funcionalidades MinIO

| Teste | Como validar |
|---|---|
| Initiate gera URLs válidas | `POST /upload/minio/initiate` retorna `uploadId` e lista de URLs |
| Chunks chegam em paralelo | Monitorar no console MinIO: **Object Browser → uploads** durante o upload |
| ETags coletados corretamente | Log no frontend: cada chunk retorna ETag no header da resposta PUT |
| Complete consolida o arquivo | Após complete, arquivo aparece inteiro no bucket |
| Cancel aborta no MinIO | Após cancel, `mc ilm rule` não lista o upload pendente |
| Listagem via API | `GET /files` lista objetos do bucket com tamanho e data |
| Download via redirect | `GET /files/{key}/download` redireciona para pre-signed URL funcional |
| Lifecycle rule ativa | Criar upload, não completar, aguardar 3 dias (ou ajustar para 1 min no teste) |

### Teste de cancelamento limpo (passo a passo)

```bash
# Terminal 1 — monitora uploads incompletos em tempo real
watch -n 2 "mc incomplete list local/uploads"

# Terminal 2 — inicia upload pelo browser e cancela em ~30%
# Ao clicar em "Cancelar", verificar que o registro some do Terminal 1
```