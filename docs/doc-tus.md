# POC — Upload de Arquivos Grandes: Cenário TUS

> **Stack:** .NET 8 · React + tus-js-client · tusdotnet · JWT · Kubernetes on-premise  
> **Estratégia:** Protocolo TUS — upload resumable com chunking, retomada automática e validação JWT.

---

## Índice

1. [Visão Geral da Arquitetura](#1-visão-geral-da-arquitetura)
2. [Estrutura de Pastas do Projeto](#2-estrutura-de-pastas-do-projeto)
3. [Backend .NET 8](#3-backend-net-8)
   - [Dependências NuGet](#31-dependências-nuget)
   - [Program.cs](#32-programcs)
   - [AuthController.cs](#33-authcontrollercs)
   - [JwtService.cs](#34-jwtservicecs)
   - [FilesController.cs](#35-filescontrollercs)
4. [Frontend React](#4-frontend-react)
   - [Instalação de dependências](#41-instalação-de-dependências)
   - [useTusUpload.ts](#42-ustusuploadts)
   - [TusUploadPage.tsx](#43-tusuploadpagetsx)
5. [Kubernetes — Manifests](#5-kubernetes--manifests)
   - [PVC (ReadWriteMany)](#51-pvc-readwritemany--pvcyaml)
   - [Deployment](#52-deployment--deploymentyaml)
   - [Secret do JWT](#53-secret-do-jwt)
   - [Ingress](#54-ingress--ingressyaml)
6. [Checklist de Validação](#6-checklist-de-validação)
7. [Próximos Passos — Cenário MinIO](#7-próximos-passos--cenário-minio)

---

## 1. Visão Geral da Arquitetura

Esta POC demonstra o upload resumable de arquivos de até 250 GB em um ambiente on-premise Kubernetes, usando o protocolo TUS. O fluxo garante que interrupções de rede não exijam reinício do upload.

| Componente | Detalhe |
|---|---|
| **Protocolo** | TUS 1.0 (resumable uploads via HTTP) |
| **Backend** | .NET 8 Web API com biblioteca tusdotnet |
| **Frontend** | React + tus-js-client |
| **Auth** | JWT Bearer Token (login simples para POC) |
| **Storage** | Volume em disco (PVC com ReadWriteMany no K8s) |
| **Ambiente** | Kubernetes on-premise |

### Fluxo Completo

1. Usuário faz login → recebe JWT
2. Frontend seleciona arquivo e inicia upload via `tus-js-client` (passa JWT no header)
3. Backend (tusdotnet) valida JWT e recebe chunks via requisições `PATCH`
4. Chunks são gravados em disco (PVC compartilhado entre pods)
5. Em caso de queda, o `tus-js-client` retoma de onde parou (via `HEAD` request)
6. Após conclusão, arquivo aparece na listagem e fica disponível para download

---

## 2. Estrutura de Pastas do Projeto

```
poc-upload/
├── backend/
│   ├── Controllers/
│   │   ├── AuthController.cs
│   │   ├── TusController.cs
│   │   └── FilesController.cs
│   ├── Services/
│   │   └── JwtService.cs
│   ├── Program.cs
│   └── appsettings.json
├── frontend/
│   ├── src/
│   │   ├── pages/
│   │   │   ├── LoginPage.tsx
│   │   │   ├── TusUploadPage.tsx
│   │   │   └── MinioUploadPage.tsx   (próxima fase)
│   │   ├── hooks/
│   │   │   └── useTusUpload.ts
│   │   └── App.tsx
│   └── package.json
└── k8s/
    ├── deployment.yaml
    ├── pvc.yaml
    └── ingress.yaml
```

---

## 3. Backend .NET 8

### 3.1 Dependências NuGet

```bash
dotnet add package tusdotnet
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add package System.IdentityModel.Tokens.Jwt
```

### 3.2 Program.cs

Configuração principal do pipeline: JWT, TUS e CORS.

```csharp
var builder = WebApplication.CreateBuilder(args);

// JWT
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        options.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Endpoint TUS — valida JWT manualmente pois o middleware tus
// intercepta antes do pipeline de autenticação do ASP.NET
app.MapTus("/upload/tus", async context => {
    var token = context.Request.Headers["Authorization"]
                    .ToString().Replace("Bearer ", "");
    if (!JwtService.ValidateToken(token,
            builder.Configuration["Jwt:Secret"]!))
        return null; // retorna 401 implicitamente

    return new DefaultTusConfiguration {
        Store = new TusDiskStore("/app/uploads/"),
        MaxAllowedUploadSizeInBytes = 300L * 1024 * 1024 * 1024, // 300 GB
        Events = new Events {
            OnFileCompleteAsync = async ctx => {
                Console.WriteLine($"Upload completo: {ctx.FileId}");
                await Task.CompletedTask;
            }
        }
    };
});

app.MapControllers();
app.Run();
```

### 3.3 AuthController.cs

Login simples para a POC. Em produção, conectar ao seu Identity Provider.

```csharp
[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;
    public AuthController(IConfiguration config) => _config = config;

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        // Credenciais hardcoded para POC
        if (req.Username != "admin" || req.Password != "poc123")
            return Unauthorized();

        var token = JwtService.GenerateToken(
            req.Username, _config["Jwt:Secret"]!);

        return Ok(new { token });
    }
}

public record LoginRequest(string Username, string Password);
```

### 3.4 JwtService.cs

```csharp
public static class JwtService
{
    public static string GenerateToken(string username, string secret)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var token = new JwtSecurityToken(
            claims: [new Claim("sub", username)],
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: new SigningCredentials(
                key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static bool ValidateToken(string token, string secret)
    {
        try {
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(token, new TokenValidationParameters {
                ValidateIssuer = false, ValidateAudience = false,
                ValidateLifetime = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(secret))
            }, out _);
            return true;
        } catch { return false; }
    }
}
```

### 3.5 FilesController.cs

Listagem e download dos arquivos enviados via TUS.

```csharp
[ApiController]
[Route("files")]
[Authorize]
public class FilesController : ControllerBase
{
    private const string UploadPath = "/app/uploads/";

    [HttpGet]
    public IActionResult List()
    {
        var files = Directory.GetFiles(UploadPath)
            .Where(f => !f.EndsWith(".metadata") && !f.EndsWith(".uploadlength"))
            .Select(f => new {
                id   = Path.GetFileName(f),
                size = new FileInfo(f).Length,
                date = new FileInfo(f).CreationTimeUtc
            });

        return Ok(files);
    }

    [HttpGet("{id}/download")]
    public IActionResult Download(string id)
    {
        var path = Path.Combine(UploadPath, id);
        if (!System.IO.File.Exists(path)) return NotFound();

        // Lê o metadata para obter o nome original do arquivo
        var metaPath = path + ".metadata";
        var filename = id;
        if (System.IO.File.Exists(metaPath))
        {
            var meta = System.IO.File.ReadAllText(metaPath);
            // metadata TUS é base64: "filename <base64>,filetype <base64>"
            var namePart = meta.Split(',')
                .FirstOrDefault(p => p.StartsWith("filename "));
            if (namePart != null)
                filename = Encoding.UTF8.GetString(
                    Convert.FromBase64String(namePart.Split(' ')[1]));
        }

        var stream = System.IO.File.OpenRead(path);
        return File(stream, "application/octet-stream",
            fileDownloadName: filename, enableRangeProcessing: true);
    }
}
```

> **`enableRangeProcessing: true`** permite que browsers e ferramentas façam download parcial (Range requests), essencial para arquivos de 250 GB.

---

## 4. Frontend React

### 4.1 Instalação de dependências

```bash
npm create vite@latest frontend -- --template react-ts
cd frontend
npm install tus-js-client axios react-router-dom
npm install -D @types/tus-js-client
```

### 4.2 useTusUpload.ts

Encapsula toda a lógica de upload, retomada, pausa, cancelamento e progresso.

```typescript
import { useRef, useState } from 'react';
import * as tus from 'tus-js-client';

interface UploadState {
  progress: number;          // 0-100
  status: 'idle' | 'uploading' | 'paused' | 'done' | 'error';
  error: string | null;
}

export function useTusUpload(token: string) {
  const uploadRef = useRef<tus.Upload | null>(null);
  const [state, setState] = useState<UploadState>({
    progress: 0, status: 'idle', error: null
  });

  const start = (file: File) => {
    const upload = new tus.Upload(file, {
      endpoint: 'http://localhost:5000/upload/tus',
      retryDelays: [0, 3000, 5000, 10000, 20000], // retomada automática
      chunkSize: 100 * 1024 * 1024, // 100 MB por chunk
      headers: { Authorization: `Bearer ${token}` },
      metadata: {
        filename: file.name,
        filetype: file.type
      },
      onProgress(bytesSent, bytesTotal) {
        setState(s => ({
          ...s,
          progress: Math.round((bytesSent / bytesTotal) * 100),
          status: 'uploading'
        }));
      },
      onSuccess() {
        setState(s => ({ ...s, status: 'done', progress: 100 }));
      },
      onError(err) {
        setState(s => ({ ...s, status: 'error', error: err.message }));
      }
    });

    // Verifica se existe upload anterior para retomar
    upload.findPreviousUploads().then(prev => {
      if (prev.length > 0) upload.resumeFromPreviousUpload(prev[0]);
      upload.start();
    });

    uploadRef.current = upload;
    setState({ progress: 0, status: 'uploading', error: null });
  };

  const pause  = () => { uploadRef.current?.abort();  setState(s => ({ ...s, status: 'paused' })); };
  const resume = () => { uploadRef.current?.start();  setState(s => ({ ...s, status: 'uploading' })); };
  const cancel = () => {
    uploadRef.current?.abort();
    setState({ progress: 0, status: 'idle', error: null });
    uploadRef.current = null;
  };

  return { ...state, start, pause, resume, cancel };
}
```

### 4.3 TusUploadPage.tsx

Tela completa com seleção de arquivo, barra de progresso, controles e listagem.

```tsx
import { useState, useEffect } from 'react';
import axios from 'axios';
import { useTusUpload } from '../hooks/useTusUpload';

export function TusUploadPage() {
  const token = localStorage.getItem('token') ?? '';
  const { progress, status, error, start, pause, resume, cancel } = useTusUpload(token);
  const [files, setFiles] = useState<any[]>([]);

  const api = axios.create({
    baseURL: 'http://localhost:5000',
    headers: { Authorization: `Bearer ${token}` }
  });

  const loadFiles = () =>
    api.get('/files').then(r => setFiles(r.data));

  useEffect(() => { loadFiles(); }, [status]);

  const handleFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) start(file);
  };

  const download = (id: string) => {
    window.open(`http://localhost:5000/files/${id}/download?token=${token}`);
  };

  return (
    <div style={{ padding: 32, maxWidth: 800, margin: '0 auto' }}>
      <h1>Upload TUS</h1>

      <input type="file" onChange={handleFile} disabled={status === 'uploading'} />

      {status !== 'idle' && (
        <div>
          <progress value={progress} max={100} style={{ width: '100%' }} />
          <p>{progress}% — {status}</p>
          {status === 'uploading' && <button onClick={pause}>Pausar</button>}
          {status === 'paused'    && <button onClick={resume}>Retomar</button>}
          {status !== 'done'      && <button onClick={cancel}>Cancelar</button>}
          {error && <p style={{ color: 'red' }}>{error}</p>}
        </div>
      )}

      <h2>Arquivos enviados</h2>
      <table border={1} cellPadding={8} style={{ width: '100%', borderCollapse: 'collapse' }}>
        <thead>
          <tr><th>ID</th><th>Tamanho</th><th>Data</th><th>Ação</th></tr>
        </thead>
        <tbody>
          {files.map(f => (
            <tr key={f.id}>
              <td>{f.id.substring(0, 8)}...</td>
              <td>{(f.size / 1024 / 1024).toFixed(1)} MB</td>
              <td>{new Date(f.date).toLocaleString('pt-BR')}</td>
              <td><button onClick={() => download(f.id)}>Download</button></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
```

> ⚠️ **Atenção:** O token no parâmetro de URL do download é apenas para POC. Em produção, use cookies HttpOnly ou implemente um endpoint que gera um token de download de curta duração.

---

## 5. Kubernetes — Manifests

### 5.1 PVC (ReadWriteMany) — pvc.yaml

O ponto crítico: todos os pods do backend precisam gravar no mesmo volume. Sem `ReadWriteMany`, chunks de um mesmo arquivo iriam para pods diferentes e o arquivo ficaria corrompido.

```yaml
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: upload-pvc
spec:
  accessModes:
    - ReadWriteMany   # CRÍTICO para múltiplos pods
  resources:
    requests:
      storage: 1Ti
  storageClassName: nfs-client  # Requer um provisioner NFS
```

> Para a POC local, você pode usar `ReadWriteOnce` com apenas 1 réplica. Para simular o ambiente real com múltiplos pods, o NFS é a opção mais simples de configurar on-premise.

### 5.2 Deployment — deployment.yaml

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: upload-backend
spec:
  replicas: 2   # Testar com 2 pods para validar o volume compartilhado
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
            - name: Jwt__Secret
              valueFrom:
                secretKeyRef:
                  name: upload-secrets
                  key: jwt-secret
          volumeMounts:
            - mountPath: /app/uploads
              name: upload-volume
      volumes:
        - name: upload-volume
          persistentVolumeClaim:
            claimName: upload-pvc
```

### 5.3 Secret do JWT

```bash
kubectl create secret generic upload-secrets \
  --from-literal=jwt-secret="sua-chave-secreta-longa-aqui-minimo-32-chars"
```

### 5.4 Ingress — ingress.yaml

Ajuste de timeout e tamanho máximo de body são obrigatórios para uploads grandes.

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: upload-ingress
  annotations:
    nginx.ingress.kubernetes.io/proxy-body-size: "0"          # Sem limite
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"    # 1 hora
    nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"    # 1 hora
    nginx.ingress.kubernetes.io/proxy-connect-timeout: "3600"
spec:
  rules:
    - host: upload.seu-dominio.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: upload-backend
                port:
                  number: 8080
```

---

## 6. Checklist de Validação

| Teste | Como validar |
|---|---|
| **Autenticação JWT** | Login retorna token; endpoints retornam 401 sem token válido |
| **Upload com chunking** | Arquivo dividido em chunks de 100 MB no frontend |
| **Barra de progresso** | Percentual atualizado em tempo real durante o upload |
| **Pausa / Retomada** | Upload pausado e retomado sem perda de dados |
| **Retomada após falha** | Derrubar a rede durante upload e verificar retomada automática |
| **Cancelamento** | Cancelar e confirmar que arquivo incompleto não aparece na listagem |
| **Listagem de arquivos** | `GET /files` retorna arquivos completos com tamanho e data |
| **Download** | Download do arquivo completo com nome original preservado |
| **Multi-pod (K8s)** | Com 2 réplicas, chunks chegam em pods diferentes sem corrupção |

### Teste de retomada (passo a passo)

1. Inicie o upload de um arquivo grande (ex: 5 GB para teste)
2. Aguarde chegar em ~40% de progresso
3. Desconecte a rede ou derrube o backend
4. Reconecte / suba o backend novamente
5. A página detecta automaticamente e retoma do ponto onde parou
6. Verifique que o progresso continua de ~40% e não reinicia do zero

---

## 7. Próximos Passos — Cenário MinIO

Após validar o cenário TUS, a POC avança para o cenário MinIO (Solução A):

- Instalar MinIO no cluster Kubernetes (Helm chart disponível)
- Implementar `MinioController.cs` para geração de pre-signed URLs
- Criar `MinioUploadPage.tsx` com upload paralelo de 5 chunks direto para o MinIO
- Comparar performance: TUS (backend como intermediário) vs MinIO (upload direto)
- Implementar cleanup de multipart uploads incompletos (Lifecycle rules)

> Consulte o documento **POC-Upload-MinIO.md** para a implementação completa deste cenário.