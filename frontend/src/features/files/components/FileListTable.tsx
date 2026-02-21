import { useMemo } from 'react'
import { useFiles } from '@/features/files/hooks/useFiles'
import { formatFileSize } from '@/features/files/utils/formatFileSize'
import type { UploadDto, UploadStatus } from '@/types'

const statusLabels: Record<UploadStatus, string> = {
  Pending: 'Pendente',
  Completed: 'Concluido',
  Corrupted: 'Corrompido',
  Cancelled: 'Cancelado',
  Failed: 'Falha',
}

const statusClassNames: Record<UploadStatus, string> = {
  Pending: 'status-badge status-badge-pending',
  Completed: 'status-badge status-badge-completed',
  Corrupted: 'status-badge status-badge-corrupted',
  Cancelled: 'status-badge status-badge-cancelled',
  Failed: 'status-badge status-badge-failed',
}

function formatUploadDate(dateValue: string): string {
  const date = new Date(dateValue)

  if (Number.isNaN(date.getTime())) {
    return '-'
  }

  const day = String(date.getDate()).padStart(2, '0')
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const year = String(date.getFullYear())
  const hours = String(date.getHours()).padStart(2, '0')
  const minutes = String(date.getMinutes()).padStart(2, '0')

  return `${day}/${month}/${year} ${hours}:${minutes}`
}

function formatScenario(scenario: string): string {
  const normalizedScenario = scenario.trim().toUpperCase()

  if (normalizedScenario === 'TUS') {
    return 'TUS'
  }

  if (normalizedScenario === 'MINIO') {
    return 'MinIO'
  }

  return scenario
}

function buildChecksumDisplay(file: UploadDto): { full: string; short: string } {
  const hash = file.actualSha256 ?? file.expectedSha256

  if (hash.length <= 16) {
    return { full: hash, short: hash }
  }

  return {
    full: hash,
    short: `${hash.slice(0, 16)}...`,
  }
}

export function FileListTable() {
  const { files, loading, error, downloadFile } = useFiles()

  const sortedFiles = useMemo(
    () =>
      [...files].sort(
        (firstFile, secondFile) =>
          new Date(secondFile.createdAt).getTime() - new Date(firstFile.createdAt).getTime(),
      ),
    [files],
  )

  if (loading) {
    return <p>Carregando arquivos...</p>
  }

  if (error) {
    return <p className="status-error">Erro ao carregar arquivos: {error}</p>
  }

  return (
    <section className="file-list-section" aria-labelledby="files-title">
      <h2 id="files-title">Arquivos enviados</h2>

      {sortedFiles.length === 0 ? (
        <p>Nenhum arquivo encontrado</p>
      ) : (
        <div className="file-list-table-wrapper">
          <table>
            <thead>
              <tr>
                <th>Nome</th>
                <th>Tamanho</th>
                <th>Cenario</th>
                <th>Status</th>
                <th>Data</th>
                <th>SHA-256</th>
                <th>Acoes</th>
              </tr>
            </thead>
            <tbody>
              {sortedFiles.map((file) => {
                const checksumDisplay = buildChecksumDisplay(file)

                return (
                  <tr key={file.id}>
                    <td>{file.fileName}</td>
                    <td>{formatFileSize(file.fileSizeBytes)}</td>
                    <td>{formatScenario(file.uploadScenario)}</td>
                    <td>
                      <span className={statusClassNames[file.status]}>
                        {statusLabels[file.status]}
                      </span>
                    </td>
                    <td>{formatUploadDate(file.createdAt)}</td>
                    <td>
                      <span className="sha-cell" title={checksumDisplay.full}>
                        {checksumDisplay.short}
                      </span>
                    </td>
                    <td>
                      <button
                        disabled={file.status !== 'Completed'}
                        onClick={() => {
                          void downloadFile(file.id, file.fileName)
                        }}
                        type="button"
                      >
                        Download
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
