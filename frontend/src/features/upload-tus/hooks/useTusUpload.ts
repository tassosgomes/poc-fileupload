import { useCallback, useRef, useState } from 'react'
import * as tus from 'tus-js-client'
import apiClient from '@/services/api'
import Sha256Worker from '@/workers/sha256Worker.ts?worker'

const TOKEN_STORAGE_KEY = 'token'
const TUS_CHUNK_SIZE_BYTES = 100 * 1024 * 1024

type UploadStatus =
  | 'idle'
  | 'hashing'
  | 'uploading'
  | 'paused'
  | 'completed'
  | 'error'
  | 'cancelled'

interface HashWorkerProgressMessage {
  type: 'progress'
  progress: number
}

interface HashWorkerResultMessage {
  type: 'result'
  hash: string
}

interface HashWorkerErrorMessage {
  type: 'error'
  message: string
}

type HashWorkerMessage =
  | HashWorkerProgressMessage
  | HashWorkerResultMessage
  | HashWorkerErrorMessage

interface RegisterTusUploadRequest {
  fileName: string
  fileSizeBytes: number
  contentType: string
  expectedSha256: string
}

interface RegisterTusUploadResponse {
  id: string
}

function normalizeErrorMessage(error: unknown): string {
  if (error instanceof Error && error.message.trim().length > 0) {
    return error.message
  }

  return 'Unable to upload file right now. Please try again.'
}

function computeSha256WithWorker(
  file: File,
  onProgress: (progress: number) => void,
): Promise<string> {
  return new Promise((resolve, reject) => {
    const worker = new Sha256Worker()

    worker.onmessage = (event: MessageEvent<HashWorkerMessage>) => {
      const message = event.data

      if (message.type === 'progress') {
        onProgress(message.progress)
        return
      }

      if (message.type === 'result') {
        worker.terminate()
        resolve(message.hash)
        return
      }

      worker.terminate()
      reject(new Error(message.message))
    }

    worker.onerror = () => {
      worker.terminate()
      reject(new Error('Failed to compute SHA-256 hash.'))
    }

    worker.postMessage({ file })
  })
}

export function useTusUpload() {
  const [status, setStatus] = useState<UploadStatus>('idle')
  const [progress, setProgress] = useState(0)
  const [hashProgress, setHashProgress] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const uploadRef = useRef<tus.Upload | null>(null)
  const uploadIdRef = useRef<string | null>(null)
  const isPausingRef = useRef(false)
  const isCancellingRef = useRef(false)

  const startUpload = useCallback(async (file: File) => {
    isPausingRef.current = false
    isCancellingRef.current = false
    setError(null)
    setProgress(0)
    setHashProgress(0)
    setStatus('hashing')

    try {
      const expectedSha256 = await computeSha256WithWorker(file, setHashProgress)
      const registerPayload: RegisterTusUploadRequest = {
        fileName: file.name,
        fileSizeBytes: file.size,
        contentType: file.type || 'application/octet-stream',
        expectedSha256,
      }

      const registerResponse = await apiClient.post<RegisterTusUploadResponse>(
        '/v1/uploads/tus/register',
        registerPayload,
      )

      const uploadId = registerResponse.data.id
      uploadIdRef.current = uploadId
      setStatus('uploading')

      const token = localStorage.getItem(TOKEN_STORAGE_KEY)

      const upload = new tus.Upload(file, {
        endpoint: '/upload/tus',
        chunkSize: TUS_CHUNK_SIZE_BYTES,
        retryDelays: [0, 1000, 3000, 5000],
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
        metadata: {
          uploadId,
          filename: file.name,
          filetype: file.type || 'application/octet-stream',
        },
        onProgress: (bytesUploaded, bytesTotal) => {
          const currentProgress =
            bytesTotal > 0 ? (bytesUploaded / bytesTotal) * 100 : 0

          setProgress(currentProgress)
        },
        onSuccess: () => {
          setProgress(100)
          setStatus('completed')
          uploadRef.current = null
        },
        onError: (uploadError) => {
          if (isCancellingRef.current) {
            return
          }

          if (isPausingRef.current) {
            isPausingRef.current = false
            return
          }

          setStatus('error')
          setError(normalizeErrorMessage(uploadError))
        },
      })

      uploadRef.current = upload
      upload.start()
    } catch (uploadError: unknown) {
      setStatus('error')
      setError(normalizeErrorMessage(uploadError))
    }
  }, [])

  const pauseUpload = useCallback(() => {
    if (uploadRef.current === null) {
      return
    }

    isPausingRef.current = true
    uploadRef.current.abort()
    setStatus('paused')
  }, [])

  const resumeUpload = useCallback(() => {
    if (uploadRef.current === null) {
      return
    }

    isPausingRef.current = false
    uploadRef.current.start()
    setStatus('uploading')
  }, [])

  const cancelUpload = useCallback(async () => {
    try {
      isCancellingRef.current = true
      uploadRef.current?.abort()

      if (uploadIdRef.current !== null) {
        await apiClient.delete(`/v1/uploads/${uploadIdRef.current}/cancel`)
      }

      setStatus('cancelled')
      uploadRef.current = null
      uploadIdRef.current = null
    } catch (cancelError: unknown) {
      setStatus('error')
      setError(normalizeErrorMessage(cancelError))
    } finally {
      isCancellingRef.current = false
    }
  }, [])

  return {
    status,
    progress,
    hashProgress,
    error,
    startUpload,
    pauseUpload,
    resumeUpload,
    cancelUpload,
  }
}
