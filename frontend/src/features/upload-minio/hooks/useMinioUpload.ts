import { useMemo } from 'react'

export function useMinioUpload() {
  const progress = useMemo(() => 0, [])

  return { progress }
}
