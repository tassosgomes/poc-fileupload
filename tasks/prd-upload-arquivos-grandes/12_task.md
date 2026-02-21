---
status: pending
parallelizable: true
blocked_by: ["3.0"]
---

<task_context>
<domain>frontend/setup</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>medium</complexity>
<dependencies>http_server</dependencies>
<unblocks>"13.0", "14.0", "15.0"</unblocks>
</task_context>

# Tarefa 12.0: Frontend — Setup, Auth e Componentes Compartilhados

## Visão Geral

Configurar o projeto React (criado na tarefa 1.0) com roteamento, layout base, serviço HTTP com interceptor JWT, página de login (feature auth), SHA-256 Web Worker e componentes compartilhados (ProgressBar, Layout). Esta tarefa pode ser iniciada após 3.0 (quando o backend já responde) mas requer 5.0 (Auth JWT) para testes completos de login.

A arquitetura do frontend é **feature-based**: cada feature (auth, upload-tus, upload-minio, files) tem seus componentes, hooks e services isolados.

## Requisitos

- Frontend deve usar TypeScript strict mode.
- Feature-based structure conforme techspec.
- SHA-256 calculado via Web Worker para não bloquear UI (RF02.2).
- Axios com interceptor para incluir JWT em todas as requisições.
- Interface minimalista focada em funcionalidade.

## Subtarefas

- [ ] 12.1 Configurar `tsconfig.json` com strict mode e path aliases
- [ ] 12.2 Configurar `vite.config.ts` com proxy para o backend em dev mode:
  - `/api` → `http://localhost:5000`
  - `/upload/tus` → `http://localhost:5000`
- [ ] 12.3 Criar tipos compartilhados em `src/types/index.ts`:
  - `UploadDto`, `LoginRequest`, `LoginResponse`, `InitiateMinioResponse`, `CompleteMinioRequest`, `PartETag`
  - `UploadStatus` type: `'Pending' | 'Completed' | 'Corrupted' | 'Cancelled' | 'Failed'`
- [ ] 12.4 Criar serviço HTTP em `src/services/api.ts`:
  - Instância Axios com `baseURL`
  - Interceptor de request: adiciona `Authorization: Bearer <token>` se token estiver no localStorage
  - Interceptor de response: redireciona para login em caso de 401
- [ ] 12.5 Criar feature Auth:
  - `src/features/auth/services/authApi.ts` — função `login(username, password)` → chama `POST /api/v1/auth/login`
  - `src/features/auth/hooks/useAuth.ts` — hook com estado de autenticação:
    - `token`, `isAuthenticated`, `login()`, `logout()`
    - Persiste token no localStorage
  - `src/features/auth/components/LoginPage.tsx`:
    - Formulário com username e password
    - Botão de login
    - Mensagem de erro para credenciais inválidas
    - Redirect para home após login
- [ ] 12.6 Criar componentes compartilhados:
  - `src/components/Layout.tsx`:
    - Header com título "Upload POC" e botão de logout
    - Navegação: links para "Upload TUS", "Upload MinIO", "Arquivos"
    - Outlet (react-router-dom)
  - `src/components/ProgressBar.tsx`:
    - Props: `progress` (0-100), `label` (string), `status` ('uploading' | 'hashing' | 'completed' | 'error')
    - Barra visual com cor conforme status
    - Texto percentual
- [ ] 12.7 Criar SHA-256 Web Worker em `src/workers/sha256Worker.ts`:
  - Recebe arquivo (File) via postMessage
  - Calcula SHA-256 via streaming (SubtleCrypto ou manual com FileReader)
  - Envia progresso periódico (% do arquivo processado)
  - Envia resultado final (hash hex string)
  - **Nota:** Para arquivos muito grandes (>10 GB), usar streaming com `FileReader.readAsArrayBuffer` em chunks
- [ ] 12.8 Configurar roteamento em `src/App.tsx`:
  - `/login` → LoginPage
  - `/` → Layout (protected, redireciona para /login se não autenticado)
  - `/upload/tus` → TusUploadPage (placeholder)
  - `/upload/minio` → MinioUploadPage (placeholder)
  - `/files` → FileListTable (placeholder)
- [ ] 12.9 Testar:
  - Página de login funcional (com backend rodando)
  - Token armazenado no localStorage
  - Requisições autenticadas incluem header Authorization
  - Navegação entre páginas funciona
  - Web Worker calcula SHA-256 de arquivo pequeno corretamente

## Sequenciamento

- Bloqueado por: 3.0 (PostgreSQL — precisa de backend respondendo para testar login)
- Desbloqueia: 13.0 (Upload TUS), 14.0 (Upload MinIO), 15.0 (Listagem)
- Paralelizável: Sim (pode ser feito em paralelo com tarefas 4.0-9.0 do backend)

## Detalhes de Implementação

### Tipos (src/types/index.ts)

```typescript
export type UploadStatus = 'Pending' | 'Completed' | 'Corrupted' | 'Cancelled' | 'Failed';

export interface UploadDto {
  id: string;
  fileName: string;
  fileSizeBytes: number;
  contentType: string;
  expectedSha256: string;
  actualSha256: string | null;
  uploadScenario: string;
  storageKey: string | null;
  status: UploadStatus;
  createdBy: string;
  createdAt: string;
  completedAt: string | null;
}

export interface LoginRequest {
  username: string;
  password: string;
}

export interface LoginResponse {
  token: string;
  expiresAt: string;
}

export interface InitiateMinioResponse {
  uploadId: string;
  storageKey: string;
  presignedUrls: string[];
  partSizeBytes: number;
  totalParts: number;
}

export interface CompleteMinioRequest {
  uploadId: string;
  parts: PartETag[];
}

export interface PartETag {
  partNumber: number;
  eTag: string;
}
```

### Serviço HTTP (src/services/api.ts)

```typescript
import axios from 'axios';

const api = axios.create({
  baseURL: '',
});

api.interceptors.request.use((config) => {
  const token = localStorage.getItem('token');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

api.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem('token');
      window.location.href = '/login';
    }
    return Promise.reject(error);
  }
);

export default api;
```

### SHA-256 Web Worker (src/workers/sha256Worker.ts)

```typescript
// Nota: Web Workers não suportam SubtleCrypto em todos os browsers para streaming.
// Alternativa: usar library "hash-wasm" ou implementação manual.

self.onmessage = async (event: MessageEvent<File>) => {
  const file = event.data;
  const chunkSize = 64 * 1024 * 1024; // 64 MB chunks para leitura
  let offset = 0;

  // Usar SubtleCrypto com streaming (se disponível)
  // Fallback: acumular chunks e calcular ao final
  const hashBuffer = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
  // Para arquivos muito grandes, usar streaming:
  // 1. Ler em chunks com FileReader
  // 2. Acumular hash incremental
  // 3. Enviar progresso: self.postMessage({ type: 'progress', progress: offset/file.size * 100 })

  const hashArray = Array.from(new Uint8Array(hashBuffer));
  const hashHex = hashArray.map(b => b.toString(16).padStart(2, '0')).join('');

  self.postMessage({ type: 'result', hash: hashHex });
};
```

### ProgressBar (src/components/ProgressBar.tsx)

```tsx
interface ProgressBarProps {
  progress: number;
  label?: string;
  status?: 'uploading' | 'hashing' | 'completed' | 'error';
}

export function ProgressBar({ progress, label, status = 'uploading' }: ProgressBarProps) {
  const colors = {
    uploading: '#3b82f6',
    hashing: '#f59e0b',
    completed: '#10b981',
    error: '#ef4444',
  };

  return (
    <div>
      {label && <p>{label}</p>}
      <div style={{ background: '#e5e7eb', borderRadius: 4, height: 20 }}>
        <div
          style={{
            width: `${Math.min(progress, 100)}%`,
            background: colors[status],
            height: '100%',
            borderRadius: 4,
            transition: 'width 0.3s',
          }}
        />
      </div>
      <span>{progress.toFixed(1)}%</span>
    </div>
  );
}
```

### Roteamento (src/App.tsx)

```tsx
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';

function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<ProtectedRoute><Layout /></ProtectedRoute>}>
          <Route index element={<Navigate to="/upload/tus" />} />
          <Route path="upload/tus" element={<TusUploadPage />} />
          <Route path="upload/minio" element={<MinioUploadPage />} />
          <Route path="files" element={<FileListTable />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
```

## Critérios de Sucesso

- Projeto React compila sem erros TypeScript
- Login com credenciais corretas armazena token e redireciona
- Login com credenciais erradas mostra mensagem de erro
- Logout limpa token e redireciona para login
- Rotas protegidas redirecionam para login sem token
- Interceptor Axios inclui token em requisições
- 401 do backend redireciona para login automaticamente
- Web Worker calcula SHA-256 corretamente para arquivo de teste
- ProgressBar renderiza com percentual e cor correta
- Navegação entre TUS/MinIO/Files funciona (páginas placeholder)
