import { useMemo } from 'react'

export function useTusUpload() {
  const progress = useMemo(() => 0, [])

  return { progress }
}
