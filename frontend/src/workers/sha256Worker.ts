import { createSHA256 } from 'hash-wasm'

const CHUNK_SIZE_BYTES = 16 * 1024 * 1024

type HashWorkerMessage = {
  file: File
}

type HashWorkerProgressMessage = {
  type: 'progress'
  progress: number
}

type HashWorkerResultMessage = {
  type: 'result'
  hash: string
}

type HashWorkerErrorMessage = {
  type: 'error'
  message: string
}

const workerScope: Pick<typeof self, 'onmessage' | 'postMessage'> = self

workerScope.onmessage = async (event: MessageEvent<HashWorkerMessage>) => {
  try {
    const { file } = event.data

    if (!(file instanceof File)) {
      workerScope.postMessage({
        type: 'error',
        message: 'Invalid payload. Expected a File object.',
      } satisfies HashWorkerErrorMessage)
      return
    }

    const hasher = await createSHA256()
    let offset = 0

    while (offset < file.size) {
      const chunk = file.slice(offset, offset + CHUNK_SIZE_BYTES)
      const chunkBuffer = await chunk.arrayBuffer()

      hasher.update(new Uint8Array(chunkBuffer))

      offset += chunk.size

      workerScope.postMessage({
        type: 'progress',
        progress: (offset / file.size) * 100,
      } satisfies HashWorkerProgressMessage)
    }

    const hash = hasher.digest('hex')
    workerScope.postMessage({ type: 'result', hash } satisfies HashWorkerResultMessage)
  } catch (error: unknown) {
    workerScope.postMessage({
      type: 'error',
      message: error instanceof Error ? error.message : 'Failed to compute file hash.',
    } satisfies HashWorkerErrorMessage)
  }
}

export {}
