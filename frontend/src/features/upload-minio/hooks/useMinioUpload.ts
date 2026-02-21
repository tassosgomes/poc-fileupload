import { useCallback, useRef, useState } from 'react'
import apiClient from '@/services/api'
import type { InitiateMinioResponse, PartETag } from '@/types'
import Sha256Worker from '@/workers/sha256Worker.ts?worker'

const PARALLEL_UPLOADS = 5
const DEFAULT_PART_SIZE_BYTES = 100 * 1024 * 1024

export type MinioUploadStatus =
  | 'idle'
  | 'hashing'
  | 'uploading'
  | 'completing'
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

interface InitiateMinioRequest {
  fileName: string
  fileSizeBytes: number
  contentType: string
  expectedSha256: string
}

class Semaphore {
  private permits: number
  private waiting: Array<() => void>

  constructor(permits: number) {
    this.permits = permits
    this.waiting = []
  }

  async acquire(): Promise<void> {
    if (this.permits > 0) {
      this.permits -= 1
      return
    }

    await new Promise<void>((resolve) => {
      this.waiting.push(resolve)
    })
  }

  release(): void {
    if (this.waiting.length > 0) {
      const next = this.waiting.shift()
      next?.()
      return
    }

    this.permits += 1
  }
}

function normalizeErrorMessage(error: unknown): string {
  if (error instanceof Error && error.message.trim().length > 0) {
    return error.message
  }

  return 'Unable to upload file right now. Please try again.'
}

function isAbortError(error: unknown): boolean {
  return (
    (error instanceof DOMException && error.name === 'AbortError') ||
    (error instanceof Error && error.name === 'AbortError')
  )
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

async function uploadChunksParallel(
  file: File,
  presignedUrls: string[],
  partSizeBytes: number,
  signal: AbortSignal,
  onProgress: (progress: number) => void,
): Promise<PartETag[]> {
  const semaphore = new Semaphore(PARALLEL_UPLOADS)
  const completedParts: PartETag[] = []
  const totalBytes = file.size
  let completedBytes = 0

  const uploadTasks = presignedUrls.map(async (url, index) => {
    await semaphore.acquire()

    try {
      if (signal.aborted) {
        throw new DOMException('Upload cancelled.', 'AbortError')
      }

      const partNumber = index + 1
      const startByte = index * partSizeBytes
      const endByte = Math.min(startByte + partSizeBytes, file.size)
      const chunk = file.slice(startByte, endByte)

      const response = await fetch(url, {
        method: 'PUT',
        body: chunk,
        signal,
      })

      if (!response.ok) {
        throw new Error(`Upload failed for part ${partNumber} (HTTP ${response.status}).`)
      }

      const etagHeader = response.headers.get('ETag')

      if (!etagHeader) {
        throw new Error(`Missing ETag header for part ${partNumber}.`)
      }

      completedParts.push({
        partNumber,
        eTag: etagHeader,
      })

      completedBytes += chunk.size
      onProgress((completedBytes / totalBytes) * 100)
    } finally {
      semaphore.release()
    }
  })

  await Promise.all(uploadTasks)

  return completedParts.sort((a, b) => a.partNumber - b.partNumber)
}

export function useMinioUpload() {
  const [status, setStatus] = useState<MinioUploadStatus>('idle')
  const [progress, setProgress] = useState(0)
  const [hashProgress, setHashProgress] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [partSizeBytes, setPartSizeBytes] = useState(DEFAULT_PART_SIZE_BYTES)
  const [totalParts, setTotalParts] = useState(0)

  const uploadIdRef = useRef<string | null>(null)
  const abortControllerRef = useRef<AbortController | null>(null)
  const isCancellingRef = useRef(false)

  const startUpload = useCallback(async (file: File) => {
    if (status === 'hashing' || status === 'uploading' || status === 'completing') {
      return
    }

    isCancellingRef.current = false
    setError(null)
    setProgress(0)
    setHashProgress(0)
    setPartSizeBytes(DEFAULT_PART_SIZE_BYTES)
    setTotalParts(0)
    setStatus('hashing')

    try {
      const expectedSha256 = await computeSha256WithWorker(file, setHashProgress)

      const initiatePayload: InitiateMinioRequest = {
        fileName: file.name,
        fileSizeBytes: file.size,
        contentType: file.type || 'application/octet-stream',
        expectedSha256,
      }

      const initiateResponse = await apiClient.post<InitiateMinioResponse>(
        '/v1/uploads/minio/initiate',
        initiatePayload,
      )

      const { uploadId, presignedUrls, partSizeBytes: responsePartSizeBytes, totalParts: responseTotalParts } =
        initiateResponse.data

      uploadIdRef.current = uploadId
      setPartSizeBytes(responsePartSizeBytes)
      setTotalParts(responseTotalParts)
      setStatus('uploading')

      const uploadAbortController = new AbortController()
      abortControllerRef.current = uploadAbortController

      const uploadedParts = await uploadChunksParallel(
        file,
        presignedUrls,
        responsePartSizeBytes,
        uploadAbortController.signal,
        setProgress,
      )

      if (uploadAbortController.signal.aborted) {
        throw new DOMException('Upload cancelled.', 'AbortError')
      }

      setStatus('completing')

      await apiClient.post('/v1/uploads/minio/complete', {
        uploadId,
        parts: uploadedParts,
      })

      setProgress(100)
      setStatus('completed')
    } catch (uploadError: unknown) {
      if (isAbortError(uploadError) || isCancellingRef.current) {
        setStatus('cancelled')
        setError(null)
        return
      }

      setStatus('error')
      setError(normalizeErrorMessage(uploadError))
    } finally {
      abortControllerRef.current = null
      if (!isCancellingRef.current) {
        uploadIdRef.current = null
      }
    }
  }, [status])

  const cancelUpload = useCallback(async () => {
    try {
      isCancellingRef.current = true
      abortControllerRef.current?.abort()

      if (uploadIdRef.current !== null) {
        await apiClient.delete('/v1/uploads/minio/abort', {
          params: { uploadId: uploadIdRef.current },
        })
      }

      setStatus('cancelled')
      setError(null)
    } catch (cancelError: unknown) {
      setStatus('error')
      setError(normalizeErrorMessage(cancelError))
    } finally {
      abortControllerRef.current = null
      uploadIdRef.current = null
    }
  }, [])

  return {
    status,
    progress,
    hashProgress,
    error,
    partSizeBytes,
    totalParts,
    startUpload,
    cancelUpload,
  }
}
