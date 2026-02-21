---
status: pending
parallelizable: true
blocked_by: ["12.0", "10.0"]
---

<task_context>
<domain>frontend/files</domain>
<type>implementation</type>
<scope>core_feature</scope>
<complexity>low</complexity>
<dependencies>http_server</dependencies>
<unblocks>"16.0"</unblocks>
</task_context>

# Tarefa 15.0: Frontend — Listagem e Download

## Visão Geral

Implementar a feature de listagem de arquivos e download no frontend. Exibe uma tabela com todos os uploads (ambos os cenários), seus status, tamanho formatado, data e checksum. Download habilitado apenas para status `Completed`. A listagem consulta `GET /api/v1/files` e o download usa `GET /api/v1/files/{id}/download`.

## Requisitos

- RF05.1: Listar todos com status, nome, tamanho, data e checksum.
- RF05.2: Mostrar status visual (PENDENTE, CONCLUÍDO, CORROMPIDO, CANCELADO).
- RF05.3: Download apenas para CONCLUÍDO.
- RF05.6: Download preserva nome original.

## Subtarefas

- [ ] 15.1 Criar hook `useFiles` em `src/features/files/hooks/useFiles.ts`:
  - Estado: `files` (UploadDto[]), `loading` (boolean), `error` (string | null)
  - `fetchFiles()`: `GET /api/v1/files` → atualiza lista
  - Auto-refresh a cada 10 segundos (polling) para atualizar status de uploads em andamento
  - `downloadFile(id: string)`: abre `GET /api/v1/files/{id}/download` em nova aba
- [ ] 15.2 Criar `FileListTable` em `src/features/files/components/FileListTable.tsx`:
  - Tabela com colunas: Nome, Tamanho, Cenário (TUS/MinIO), Status, Data, SHA-256, Ações
  - Tamanho formatado (KB/MB/GB)
  - Status com badge colorido:
    - Pending: amarelo
    - Completed: verde
    - Corrupted: vermelho
    - Cancelled: cinza
    - Failed: vermelho escuro
  - SHA-256 truncado (primeiros 16 chars + "...") com tooltip para ver completo
  - Botão "Download" habilitado apenas para status `Completed`
  - Data formatada: `dd/MM/yyyy HH:mm`
  - Mensagem "Nenhum arquivo encontrado" quando lista vazia
- [ ] 15.3 Implementar formatação de tamanho:
  - Utility function `formatFileSize(bytes: number)`:
    - < 1024: `X B`
    - < 1024²: `X.X KB`
    - < 1024³: `X.X MB`
    - < 1024⁴: `X.X GB`
- [ ] 15.4 Testar:
  - Tabela exibe uploads de ambos os cenários
  - Status é exibido com badge colorido
  - Download funciona para arquivo Completed (TUS e MinIO)
  - Download desabilitado para status não-Completed
  - Polling atualiza status automaticamente

## Sequenciamento

- Bloqueado por: 12.0 (Frontend Setup), 10.0 (Listagem e Download — precisa dos endpoints backend)
- Desbloqueia: 16.0 (K8s — validação end-to-end)
- Paralelizável: Sim (pode ser feito em paralelo com 13.0 e 14.0)

## Detalhes de Implementação

### useFiles Hook

```typescript
import { useState, useEffect, useCallback } from 'react';
import api from '../../../services/api';
import { UploadDto } from '../../../types';

export function useFiles() {
  const [files, setFiles] = useState<UploadDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchFiles = useCallback(async () => {
    try {
      const { data } = await api.get<UploadDto[]>('/api/v1/files');
      setFiles(data);
      setError(null);
    } catch (err: any) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchFiles();
    const interval = setInterval(fetchFiles, 10000); // Polling 10s
    return () => clearInterval(interval);
  }, [fetchFiles]);

  const downloadFile = (id: string) => {
    const token = localStorage.getItem('token');
    // Para TUS: backend serve o arquivo diretamente
    // Para MinIO: backend faz redirect para pre-signed URL
    window.open(`/api/v1/files/${id}/download`, '_blank');
  };

  return { files, loading, error, fetchFiles, downloadFile };
}
```

### FileListTable

```tsx
import { useFiles } from '../hooks/useFiles';
import { UploadDto, UploadStatus } from '../../../types';

const statusColors: Record<UploadStatus, string> = {
  Pending: '#f59e0b',
  Completed: '#10b981',
  Corrupted: '#ef4444',
  Cancelled: '#9ca3af',
  Failed: '#dc2626',
};

const statusLabels: Record<UploadStatus, string> = {
  Pending: 'Pendente',
  Completed: 'Concluído',
  Corrupted: 'Corrompido',
  Cancelled: 'Cancelado',
  Failed: 'Falha',
};

export function FileListTable() {
  const { files, loading, error, downloadFile } = useFiles();

  if (loading) return <p>Carregando...</p>;
  if (error) return <p style={{ color: 'red' }}>Erro: {error}</p>;
  if (files.length === 0) return <p>Nenhum arquivo encontrado.</p>;

  return (
    <div>
      <h2>Arquivos</h2>
      <table>
        <thead>
          <tr>
            <th>Nome</th>
            <th>Tamanho</th>
            <th>Cenário</th>
            <th>Status</th>
            <th>Data</th>
            <th>SHA-256</th>
            <th>Ações</th>
          </tr>
        </thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.id}>
              <td>{file.fileName}</td>
              <td>{formatFileSize(file.fileSizeBytes)}</td>
              <td>{file.uploadScenario}</td>
              <td>
                <span style={{
                  backgroundColor: statusColors[file.status],
                  color: 'white',
                  padding: '2px 8px',
                  borderRadius: 4,
                  fontSize: 12,
                }}>
                  {statusLabels[file.status]}
                </span>
              </td>
              <td>{new Date(file.createdAt).toLocaleString('pt-BR')}</td>
              <td title={file.actualSha256 ?? file.expectedSha256}>
                {(file.actualSha256 ?? file.expectedSha256).substring(0, 16)}...
              </td>
              <td>
                <button
                  onClick={() => downloadFile(file.id)}
                  disabled={file.status !== 'Completed'}
                >
                  Download
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
}
```

## Critérios de Sucesso

- Tabela lista uploads de ambos os cenários (TUS e MinIO) ordenados por data
- Status exibido com badge colorido conforme estado
- Tamanho formatado corretamente (KB, MB, GB)
- SHA-256 truncado com tooltip completo
- Download funciona para arquivo `Completed` via TUS (serve arquivo)
- Download funciona para arquivo `Completed` via MinIO (redirect para pre-signed URL)
- Botão Download desabilitado para status `Pending`, `Corrupted`, `Cancelled`, `Failed`
- Polling a cada 10s atualiza status automaticamente
- Mensagem "Nenhum arquivo encontrado" quando lista vazia
- Data formatada em pt-BR
