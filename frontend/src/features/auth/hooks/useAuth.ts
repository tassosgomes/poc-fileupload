import { useCallback } from 'react'
import { authenticate } from '@/features/auth/services/authApi'

export function useAuth() {
  const login = useCallback(() => {
    void authenticate()
  }, [])

  return { login }
}
