import { useCallback, useEffect, useState } from 'react'
import axios from 'axios'
import apiClient from '@/services/api'
import type { UploadDto } from '@/types'

const FILES_POLLING_INTERVAL_MS = 10_000

function extractFilenameFromContentDisposition(headerValue: string | undefined): string | null {
  if (!headerValue || headerValue.trim().length === 0) {
    return null
  }

  const utf8Match = headerValue.match(/filename\*=UTF-8''([^;]+)/i)
  if (utf8Match?.[1]) {
    return decodeURIComponent(utf8Match[1])
  }

  const quotedMatch = headerValue.match(/filename="([^"]+)"/i)
  if (quotedMatch?.[1]) {
    return quotedMatch[1]
  }

  const simpleMatch = headerValue.match(/filename=([^;]+)/i)
  if (simpleMatch?.[1]) {
    return simpleMatch[1].trim()
  }

  return null
}

function normalizeErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error)) {
    if (typeof error.response?.data?.detail === 'string') {
      return error.response.data.detail
    }

    if (typeof error.message === 'string' && error.message.trim().length > 0) {
      return error.message
    }
  }

  if (error instanceof Error && error.message.trim().length > 0) {
    return error.message
  }

  return 'Nao foi possivel carregar os arquivos.'
}

export function useFiles() {
  const [files, setFiles] = useState<UploadDto[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const fetchFiles = useCallback(async () => {
    try {
      const response = await apiClient.get<UploadDto[]>('/v1/files')
      setFiles(response.data)
      setError(null)
    } catch (fetchError: unknown) {
      setError(normalizeErrorMessage(fetchError))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void fetchFiles()

    const intervalId = window.setInterval(() => {
      void fetchFiles()
    }, FILES_POLLING_INTERVAL_MS)

    return () => {
      window.clearInterval(intervalId)
    }
  }, [fetchFiles])

  const downloadFile = useCallback(async (id: string, fallbackFileName?: string) => {
    try {
      const response = await apiClient.get<Blob>(`/v1/files/${id}/download`, {
        responseType: 'blob',
      })

      const contentDisposition = response.headers['content-disposition'] as string | undefined
      const responseFileName = extractFilenameFromContentDisposition(contentDisposition)
      const fileName = responseFileName ?? fallbackFileName ?? `download-${id}`

      const objectUrl = window.URL.createObjectURL(response.data)
      const link = document.createElement('a')
      link.href = objectUrl
      link.download = fileName
      link.rel = 'noopener noreferrer'
      document.body.append(link)
      link.click()
      link.remove()
      window.URL.revokeObjectURL(objectUrl)
      setError(null)
    } catch (downloadError: unknown) {
      setError(normalizeErrorMessage(downloadError))
    }
  }, [])

  return {
    files,
    loading,
    error,
    fetchFiles,
    downloadFile,
  }
}
