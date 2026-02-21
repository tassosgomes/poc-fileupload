import { useState } from 'react'
import type { ChangeEvent } from 'react'
import { ProgressBar } from '@/components/ProgressBar'
import { useMinioUpload } from '@/features/upload-minio/hooks/useMinioUpload'

function formatFileSize(sizeInBytes: number): string {
  if (sizeInBytes === 0) {
    return '0 B'
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const unitIndex = Math.min(
    Math.floor(Math.log(sizeInBytes) / Math.log(1024)),
    units.length - 1,
  )
  const value = sizeInBytes / 1024 ** unitIndex

  return `${value.toFixed(value >= 10 ? 1 : 2)} ${units[unitIndex]}`
}

export function MinioUploadPage() {
  const [file, setFile] = useState<File | null>(null)
  const {
    status,
    progress,
    hashProgress,
    error,
    partSizeBytes,
    totalParts,
    startUpload,
    cancelUpload,
  } = useMinioUpload()

  const isBusy = status === 'hashing' || status === 'uploading' || status === 'completing'
  const canStartUpload = file !== null && !isBusy

  const chunkCount =
    file !== null
      ? totalParts > 0
        ? totalParts
        : Math.max(1, Math.ceil(file.size / partSizeBytes))
      : 0

  const handleFileChange = (event: ChangeEvent<HTMLInputElement>) => {
    if (isBusy) {
      return
    }

    setFile(event.target.files?.[0] ?? null)
  }

  return (
    <section className="minio-upload-page" aria-labelledby="minio-upload-title">
      <h2 id="minio-upload-title">Upload via MinIO</h2>
      <p>
        Upload multipart direto para o MinIO com chunks em paralelo (5 simultaneos,
        sem pause/resume).
      </p>

      <label className="file-picker" htmlFor="minio-file-input">
        <span>Selecionar arquivo</span>
        <input
          disabled={isBusy}
          id="minio-file-input"
          onChange={handleFileChange}
          type="file"
        />
      </label>

      {file ? (
        <div className="file-summary">
          <p>
            <strong>Arquivo:</strong> {file.name}
          </p>
          <p>
            <strong>Tamanho:</strong> {formatFileSize(file.size)}
          </p>
          <p>
            <strong>Chunks:</strong> {chunkCount} x {formatFileSize(partSizeBytes)} | Paralelo: 5
          </p>
        </div>
      ) : null}

      <button
        disabled={!canStartUpload || file === null}
        onClick={() => {
          if (file !== null) {
            void startUpload(file)
          }
        }}
        type="button"
      >
        Iniciar Upload
      </button>

      {status === 'hashing' ? (
        <ProgressBar
          progress={hashProgress}
          label="Calculando SHA-256..."
          status="hashing"
        />
      ) : null}

      {status === 'uploading' || status === 'completing' ? (
        <div className="upload-progress-section">
          <ProgressBar
            progress={progress}
            label={status === 'completing' ? 'Finalizando upload multipart...' : 'Enviando chunks para o MinIO...'}
            status="uploading"
          />
          <div className="upload-actions">
            <button
              disabled={status !== 'uploading'}
              onClick={() => {
                void cancelUpload()
              }}
              type="button"
            >
              Cancelar
            </button>
          </div>
        </div>
      ) : null}

      {status === 'completed' ? (
        <p className="status-success">Upload concluido com sucesso!</p>
      ) : null}
      {status === 'error' && error ? (
        <p className="status-error">Erro no upload: {error}</p>
      ) : null}
      {status === 'cancelled' ? (
        <p className="status-neutral">Upload cancelado.</p>
      ) : null}
    </section>
  )
}
