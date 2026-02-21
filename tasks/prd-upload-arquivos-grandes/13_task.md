---
status: pending
parallelizable: true
blocked_by: ["12.0"]
---

<task_context>
<domain>frontend/upload-tus</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>high</complexity>
<dependencies>http_server</dependencies>
<unblocks>"16.0"</unblocks>
</task_context>

# Tarefa 13.0: Frontend — Upload TUS

## Visão Geral

Implementar a feature de upload via protocolo TUS no frontend usando `tus-js-client`. O fluxo inclui: seleção de arquivo → cálculo SHA-256 via Web Worker → registro de metadados no backend → upload chunked (100 MB) com progresso em tempo real → controles de Pausar, Retomar e Cancelar.

## Requisitos

- RF03.1: Chunks de 100 MB via protocolo TUS.
- RF03.2: Barra de progresso em tempo real.
- RF03.3: Pause/Resume sem retransmitir dados.
- RF03.4: Retry automático com backoff após queda de rede.
- RF03.5: Cancelamento remove arquivo e atualiza status.
- RF03.7: Cancel atualiza status para CANCELADO.

## Subtarefas

- [ ] 13.1 Criar hook `useTusUpload` em `src/features/upload-tus/hooks/useTusUpload.ts`:
  - Estado: `status` ('idle' | 'hashing' | 'uploading' | 'paused' | 'completed' | 'error' | 'cancelled')
  - Estado: `progress` (0-100), `hashProgress` (0-100), `error` (string | null)
  - `startUpload(file: File)`:
    1. Set status 'hashing'
    2. Calcular SHA-256 via Web Worker (com progresso)
    3. `POST /api/v1/uploads/tus/register` com metadados
    4. Set status 'uploading'
    5. Criar `tus.Upload` com endpoint `/upload/tus`, chunkSize 100MB, metadata `{ uploadId }`
    6. Iniciar upload
  - `pauseUpload()`: `upload.abort()` + set status 'paused'
  - `resumeUpload()`: `upload.start()` + set status 'uploading'
  - `cancelUpload()`: `upload.abort()` + `DELETE /api/v1/uploads/{id}/cancel` + set status 'cancelled'
- [ ] 13.2 Criar `TusUploadPage` em `src/features/upload-tus/components/TusUploadPage.tsx`:
  - Input de seleção de arquivo (drag-and-drop ou file picker)
  - Exibir nome e tamanho do arquivo selecionado
  - Botão "Iniciar Upload"
  - ProgressBar para hashing (status 'hashing', amarelo)
  - ProgressBar para upload (status 'uploading', azul)
  - Botão "Pausar" (habilitado quando uploading)
  - Botão "Retomar" (habilitado quando paused)
  - Botão "Cancelar" (habilitado quando uploading ou paused)
  - Mensagem de sucesso quando completed
  - Mensagem de erro com detalhes
- [ ] 13.3 Integrar sha256Worker para cálculo de hash:
  - Instanciar worker
  - Passar arquivo via postMessage
  - Escutar progresso e resultado
  - Atualizar `hashProgress` conforme worker envia progresso
- [ ] 13.4 Configurar `tus-js-client`:
  - `endpoint`: `/upload/tus`
  - `chunkSize`: 100 * 1024 * 1024 (100 MB)
  - `retryDelays`: [0, 1000, 3000, 5000] (backoff progressivo)
  - `headers`: `{ Authorization: 'Bearer <token>' }`
  - `metadata`: `{ uploadId, filename, filetype }`
  - `onProgress`: atualizar `progress`
  - `onSuccess`: set status 'completed'
  - `onError`: set status 'error' + log error
- [ ] 13.5 Testar:
  - Upload de arquivo pequeno (~10 MB) funciona end-to-end
  - Upload de arquivo médio (~500 MB) com progresso visível
  - Pause: upload para, resume retoma do ponto correto
  - Cancel: upload cancela, backend atualiza status
  - Erro de rede: retry automático com backoff

## Sequenciamento

- Bloqueado por: 12.0 (Frontend Setup — precisa de roteamento, api service, Web Worker, ProgressBar)
- Desbloqueia: 16.0 (K8s — validação end-to-end)
- Paralelizável: Sim (pode ser feito em paralelo com 14.0 e 15.0)

## Detalhes de Implementação

### useTusUpload Hook

```typescript
import * as tus from 'tus-js-client';
import api from '../../../services/api';

interface UseTusUploadReturn {
  status: 'idle' | 'hashing' | 'uploading' | 'paused' | 'completed' | 'error' | 'cancelled';
  progress: number;
  hashProgress: number;
  error: string | null;
  startUpload: (file: File) => Promise<void>;
  pauseUpload: () => void;
  resumeUpload: () => void;
  cancelUpload: () => Promise<void>;
}

export function useTusUpload(): UseTusUploadReturn {
  const [status, setStatus] = useState<UseTusUploadReturn['status']>('idle');
  const [progress, setProgress] = useState(0);
  const [hashProgress, setHashProgress] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const uploadRef = useRef<tus.Upload | null>(null);
  const uploadIdRef = useRef<string | null>(null);

  const startUpload = async (file: File) => {
    try {
      // 1. Hash SHA-256
      setStatus('hashing');
      const sha256 = await computeSha256WithWorker(file, setHashProgress);

      // 2. Registrar no backend
      const { data } = await api.post('/api/v1/uploads/tus/register', {
        fileName: file.name,
        fileSizeBytes: file.size,
        contentType: file.type || 'application/octet-stream',
        expectedSha256: sha256,
      });
      uploadIdRef.current = data.id;

      // 3. Iniciar upload TUS
      setStatus('uploading');
      const token = localStorage.getItem('token');

      const upload = new tus.Upload(file, {
        endpoint: '/upload/tus',
        chunkSize: 100 * 1024 * 1024,
        retryDelays: [0, 1000, 3000, 5000],
        headers: { Authorization: `Bearer ${token}` },
        metadata: {
          uploadId: data.id,
          filename: file.name,
          filetype: file.type || 'application/octet-stream',
        },
        onProgress: (bytesUploaded, bytesTotal) => {
          setProgress((bytesUploaded / bytesTotal) * 100);
        },
        onSuccess: () => {
          setStatus('completed');
        },
        onError: (err) => {
          setStatus('error');
          setError(err.message);
        },
      });

      uploadRef.current = upload;
      upload.start();
    } catch (err: any) {
      setStatus('error');
      setError(err.message);
    }
  };

  const pauseUpload = () => {
    uploadRef.current?.abort();
    setStatus('paused');
  };

  const resumeUpload = () => {
    setStatus('uploading');
    uploadRef.current?.start();
  };

  const cancelUpload = async () => {
    uploadRef.current?.abort();
    if (uploadIdRef.current) {
      await api.delete(`/api/v1/uploads/${uploadIdRef.current}/cancel`);
    }
    setStatus('cancelled');
  };

  return { status, progress, hashProgress, error, startUpload, pauseUpload, resumeUpload, cancelUpload };
}
```

### TusUploadPage

```tsx
export function TusUploadPage() {
  const [file, setFile] = useState<File | null>(null);
  const { status, progress, hashProgress, error, startUpload, pauseUpload, resumeUpload, cancelUpload } = useTusUpload();

  return (
    <div>
      <h2>Upload via TUS</h2>

      <input type="file" onChange={(e) => setFile(e.target.files?.[0] ?? null)} disabled={status === 'uploading'} />

      {file && (
        <div>
          <p>{file.name} — {formatFileSize(file.size)}</p>
          <button onClick={() => startUpload(file)} disabled={status !== 'idle'}>
            Iniciar Upload
          </button>
        </div>
      )}

      {status === 'hashing' && <ProgressBar progress={hashProgress} label="Calculando SHA-256..." status="hashing" />}

      {(status === 'uploading' || status === 'paused') && (
        <>
          <ProgressBar progress={progress} label="Enviando arquivo..." status="uploading" />
          <button onClick={pauseUpload} disabled={status !== 'uploading'}>Pausar</button>
          <button onClick={resumeUpload} disabled={status !== 'paused'}>Retomar</button>
          <button onClick={cancelUpload}>Cancelar</button>
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

- Seleção de arquivo mostra nome e tamanho formatado
- SHA-256 é calculado via Web Worker com barra de progresso
- Upload TUS inicia após hash e registra metadados no backend
- Barra de progresso atualiza em tempo real durante upload
- Pause: upload para imediatamente, botão "Retomar" habilita
- Resume: upload retoma do offset correto (sem retransmissão)
- Cancel: upload cancela, backend atualiza status para CANCELADO
- Retry automático após erro de rede (com backoff 0s, 1s, 3s, 5s)
- Status 'completed' mostra mensagem de sucesso
- Status 'error' mostra detalhes do erro
- Upload de arquivo ≥ 1 GB funciona sem problemas de memória
