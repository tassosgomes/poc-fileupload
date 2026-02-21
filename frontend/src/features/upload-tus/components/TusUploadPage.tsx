import { useRef, useState } from 'react'
import type { ChangeEvent, DragEvent, KeyboardEvent } from 'react'
import { ProgressBar } from '@/components/ProgressBar'
import { useTusUpload } from '@/features/upload-tus/hooks/useTusUpload'

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

export function TusUploadPage() {
  const [file, setFile] = useState<File | null>(null)
  const [isDragActive, setIsDragActive] = useState(false)
  const inputRef = useRef<HTMLInputElement | null>(null)

  const {
    status,
    progress,
    hashProgress,
    error,
    startUpload,
    pauseUpload,
    resumeUpload,
    cancelUpload,
  } = useTusUpload()

  const isUploadInProgress = status === 'uploading' || status === 'paused'
  const canStartUpload =
    file !== null && !['hashing', 'uploading', 'paused'].includes(status)

  const handleFileSelection = (selectedFile: File | null) => {
    if (selectedFile === null || isUploadInProgress || status === 'hashing') {
      return
    }

    setFile(selectedFile)
  }

  const handleInputChange = (event: ChangeEvent<HTMLInputElement>) => {
    handleFileSelection(event.target.files?.[0] ?? null)
  }

  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault()
    setIsDragActive(false)

    handleFileSelection(event.dataTransfer.files[0] ?? null)
  }

  const handleDragOver = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault()
    if (!isUploadInProgress && status !== 'hashing') {
      setIsDragActive(true)
    }
  }

  const handleDragLeave = () => {
    setIsDragActive(false)
  }

  const handleDropzoneKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Enter' && event.key !== ' ') {
      return
    }

    event.preventDefault()

    if (!isUploadInProgress && status !== 'hashing') {
      inputRef.current?.click()
    }
  }

  return (
    <section className="tus-upload-page" aria-labelledby="tus-upload-title">
      <h2 id="tus-upload-title">Upload via TUS</h2>
      <p>Selecione um arquivo para calcular SHA-256 e enviar em chunks de 100 MB.</p>

      <input
        ref={inputRef}
        className="file-input-hidden"
        onChange={handleInputChange}
        type="file"
      />

      <div
        aria-disabled={isUploadInProgress || status === 'hashing'}
        className={`dropzone ${isDragActive ? 'dropzone-active' : ''}`}
        onDragLeave={handleDragLeave}
        onDragOver={handleDragOver}
        onDrop={handleDrop}
        onKeyDown={handleDropzoneKeyDown}
        role="button"
        tabIndex={0}
      >
        <p>Arraste o arquivo aqui ou use o seletor.</p>
        <button
          disabled={isUploadInProgress || status === 'hashing'}
          onClick={() => inputRef.current?.click()}
          type="button"
        >
          Escolher arquivo
        </button>
      </div>

      {file ? (
        <div className="file-summary">
          <p>
            <strong>Arquivo:</strong> {file.name}
          </p>
          <p>
            <strong>Tamanho:</strong> {formatFileSize(file.size)}
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

      {status === 'uploading' || status === 'paused' ? (
        <div className="upload-progress-section">
          <ProgressBar
            progress={progress}
            label="Enviando arquivo..."
            status="uploading"
          />

          <div className="upload-actions">
            <button
              disabled={status !== 'uploading'}
              onClick={pauseUpload}
              type="button"
            >
              Pausar
            </button>
            <button
              disabled={status !== 'paused'}
              onClick={resumeUpload}
              type="button"
            >
              Retomar
            </button>
            <button
              disabled={status !== 'uploading' && status !== 'paused'}
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
