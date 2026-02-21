---
status: pending
parallelizable: true
blocked_by: ["12.0"]
---

<task_context>
<domain>frontend/upload-minio</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>high</complexity>
<dependencies>http_server, external_apis</dependencies>
<unblocks>"16.0"</unblocks>
</task_context>

# Tarefa 14.0: Frontend — Upload MinIO

## Visão Geral

Implementar a feature de upload direto ao MinIO via pre-signed URLs no frontend. O fluxo inclui: seleção de arquivo → cálculo SHA-256 via Web Worker → initiate multipart no backend → upload de chunks em paralelo (5 simultâneos, 100 MB cada) diretamente ao MinIO → complete multipart no backend. Os bytes **não passam pelo backend**.

## Requisitos

- RF04.1: Frontend solicita pre-signed URLs ao backend.
- RF04.2: Chunks enviados diretamente ao MinIO via PUT, 5 em paralelo.
- RF04.3: Barra de progresso em tempo real.
- RF04.4: Frontend envia ETags ao backend para consolidação.
- RF04.5: Cancelamento chama abort no backend.
- RF04.7: Suporte a arquivos até 250 GB.

## Subtarefas

- [ ] 14.1 Criar hook `useMinioUpload` em `src/features/upload-minio/hooks/useMinioUpload.ts`:
  - Estado: `status` ('idle' | 'hashing' | 'uploading' | 'completing' | 'completed' | 'error' | 'cancelled')
  - Estado: `progress` (0-100), `hashProgress` (0-100), `error` (string | null)
  - `startUpload(file: File)`:
    1. Set status 'hashing'
    2. Calcular SHA-256 via Web Worker (com progresso)
    3. `POST /api/v1/uploads/minio/initiate` com metadados → recebe `uploadId` + pre-signed URLs
    4. Set status 'uploading'
    5. Fatiar arquivo em chunks de `partSizeBytes`
    6. Enviar chunks em paralelo (máx 5 simultâneos) via `PUT` nas pre-signed URLs
    7. Coletar ETags de cada resposta
    8. Set status 'completing'
    9. `POST /api/v1/uploads/minio/complete` com ETags
    10. Set status 'completed'
  - `cancelUpload()`: abortar requests em andamento + `DELETE /api/v1/uploads/minio/abort?uploadId=...`
- [ ] 14.2 Implementar upload paralelo com controle de concorrência:
  - Pool de 5 workers simultâneos
  - Cada worker faz `PUT` de um chunk na pre-signed URL correspondente
  - Coletar resposta `ETag` do header da resposta
  - Atualizar progresso global (total bytes enviados / total bytes)
  - Usar `AbortController` para cancelamento
- [ ] 14.3 Criar `MinioUploadPage` em `src/features/upload-minio/components/MinioUploadPage.tsx`:
  - Input de seleção de arquivo
  - Exibir nome e tamanho do arquivo
  - Botão "Iniciar Upload"
  - ProgressBar para hashing
  - ProgressBar para upload
  - Botão "Cancelar" (habilitado durante upload)
  - Mensagem de sucesso/erro
  - **Nota:** Pause/Resume não suportado no cenário MinIO (fora de escopo do PRD)
- [ ] 14.4 Testar:
  - Upload de arquivo pequeno (~10 MB) funciona end-to-end
  - Upload de arquivo médio (~500 MB) com 5 chunks em paralelo
  - Cancel: requests abortados, multipart cancelado no MinIO
  - ETags coletados corretamente e enviados ao backend
  - Progresso atualiza em tempo real

## Sequenciamento

- Bloqueado por: 12.0 (Frontend Setup — precisa de roteamento, api service, Web Worker, ProgressBar)
- Desbloqueia: 16.0 (K8s — validação end-to-end)
- Paralelizável: Sim (pode ser feito em paralelo com 13.0 e 15.0)

## Detalhes de Implementação

### useMinioUpload Hook

```typescript
import api from '../../../services/api';

const PARALLEL_UPLOADS = 5;
const PART_SIZE = 100 * 1024 * 1024; // 100 MB

export function useMinioUpload() {
  const [status, setStatus] = useState<string>('idle');
  const [progress, setProgress] = useState(0);
  const [hashProgress, setHashProgress] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
  const uploadIdRef = useRef<string | null>(null);

  const startUpload = async (file: File) => {
    try {
      // 1. Hash SHA-256
      setStatus('hashing');
      const sha256 = await computeSha256WithWorker(file, setHashProgress);

      // 2. Initiate multipart
      const { data } = await api.post<InitiateMinioResponse>('/api/v1/uploads/minio/initiate', {
        fileName: file.name,
        fileSizeBytes: file.size,
        contentType: file.type || 'application/octet-stream',
        expectedSha256: sha256,
      });
      uploadIdRef.current = data.uploadId;

      // 3. Upload chunks em paralelo
      setStatus('uploading');
      const abortController = new AbortController();
      abortControllerRef.current = abortController;

      const etags = await uploadChunksParallel(
        file, data.presignedUrls, data.partSizeBytes,
        abortController.signal, setProgress
      );

      // 4. Complete multipart
      setStatus('completing');
      await api.post('/api/v1/uploads/minio/complete', {
        uploadId: data.uploadId,
        parts: etags,
      });

      setStatus('completed');
    } catch (err: any) {
      if (err.name === 'AbortError') {
        setStatus('cancelled');
      } else {
        setStatus('error');
        setError(err.message);
      }
    }
  };

  const cancelUpload = async () => {
    abortControllerRef.current?.abort();
    if (uploadIdRef.current) {
      await api.delete(`/api/v1/uploads/minio/abort?uploadId=${uploadIdRef.current}`);
    }
    setStatus('cancelled');
  };

  return { status, progress, hashProgress, error, startUpload, cancelUpload };
}
```

### Upload Paralelo com Controle de Concorrência

```typescript
async function uploadChunksParallel(
  file: File,
  presignedUrls: string[],
  partSize: number,
  signal: AbortSignal,
  onProgress: (progress: number) => void,
): Promise<PartETag[]> {
  const totalParts = presignedUrls.length;
  const etags: PartETag[] = [];
  let completedBytes = 0;
  const totalBytes = file.size;

  // Semáforo para limitar paralelismo
  const semaphore = new Semaphore(PARALLEL_UPLOADS);

  const promises = presignedUrls.map(async (url, index) => {
    await semaphore.acquire();
    try {
      if (signal.aborted) throw new DOMException('Aborted', 'AbortError');

      const start = index * partSize;
      const end = Math.min(start + partSize, file.size);
      const chunk = file.slice(start, end);

      const response = await fetch(url, {
        method: 'PUT',
        body: chunk,
        signal,
      });

      if (!response.ok) throw new Error(`Upload part ${index + 1} failed: ${response.status}`);

      const etag = response.headers.get('ETag')!;
      etags.push({ partNumber: index + 1, eTag: etag });

      completedBytes += (end - start);
      onProgress((completedBytes / totalBytes) * 100);
    } finally {
      semaphore.release();
    }
  });

  await Promise.all(promises);

  // Ordenar por partNumber
  return etags.sort((a, b) => a.partNumber - b.partNumber);
}

// Semáforo simples para controle de concorrência
class Semaphore {
  private permits: number;
  private waiting: (() => void)[] = [];

  constructor(permits: number) { this.permits = permits; }

  async acquire(): Promise<void> {
    if (this.permits > 0) {
      this.permits--;
      return;
    }
    return new Promise((resolve) => this.waiting.push(resolve));
  }

  release(): void {
    if (this.waiting.length > 0) {
      this.waiting.shift()!();
    } else {
      this.permits++;
    }
  }
}
```

### MinioUploadPage

```tsx
export function MinioUploadPage() {
  const [file, setFile] = useState<File | null>(null);
  const { status, progress, hashProgress, error, startUpload, cancelUpload } = useMinioUpload();

  return (
    <div>
      <h2>Upload via MinIO (Direto)</h2>

      <input type="file" onChange={(e) => setFile(e.target.files?.[0] ?? null)}
             disabled={status === 'uploading' || status === 'completing'} />

      {file && (
        <div>
          <p>{file.name} — {formatFileSize(file.size)}</p>
          <p>Chunks: {Math.ceil(file.size / (100 * 1024 * 1024))} × 100 MB | Paralelo: 5</p>
          <button onClick={() => startUpload(file)} disabled={status !== 'idle'}>
            Iniciar Upload
          </button>
        </div>
      )}

      {status === 'hashing' && <ProgressBar progress={hashProgress} label="Calculando SHA-256..." status="hashing" />}

      {(status === 'uploading' || status === 'completing') && (
        <>
          <ProgressBar progress={progress}
                       label={status === 'completing' ? 'Finalizando...' : 'Enviando ao MinIO...'}
                       status="uploading" />
          <button onClick={cancelUpload} disabled={status === 'completing'}>Cancelar</button>
        </>
      )}

      {status === 'completed' && <p style={{ color: 'green' }}>Upload concluído com sucesso!</p>}
      {status === 'error' && <p style={{ color: 'red' }}>Erro: {error}</p>}
      {status === 'cancelled' && <p style={{ color: 'gray' }}>Upload cancelado.</p>}
    </div>
  );
}
```

## Critérios de Sucesso

- Initiate retorna pre-signed URLs e uploadId
- Chunks são enviados diretamente ao MinIO (não passam pelo backend)
- 5 chunks enviados em paralelo (verificar via Network tab do DevTools)
- ETags coletados corretamente dos headers de resposta
- Complete consolida arquivo no MinIO
- Progresso atualiza em tempo real
- Cancel: requests abortados via AbortController, multipart cancelado no MinIO
- Upload de arquivo ≥ 1 GB funciona com progresso
- Frontend CPU/RAM estável (bytes não ficam em memória — `file.slice` é lazy)
- Nenhum byte passa pelo backend (verificar CPU/RAM do backend estável)
