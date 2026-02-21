import { useCallback, useEffect, useMemo, useState } from 'react'
import { login as loginRequest } from '@/features/auth/services/authApi'

const TOKEN_STORAGE_KEY = 'token'
const AUTH_STORAGE_EVENT = 'auth:token-changed'

export function useAuth() {
  const [token, setToken] = useState<string | null>(() =>
    localStorage.getItem(TOKEN_STORAGE_KEY),
  )

  useEffect(() => {
    const handleTokenChange = () => {
      setToken(localStorage.getItem(TOKEN_STORAGE_KEY))
    }

    window.addEventListener('storage', handleTokenChange)
    window.addEventListener(AUTH_STORAGE_EVENT, handleTokenChange)

    return () => {
      window.removeEventListener('storage', handleTokenChange)
      window.removeEventListener(AUTH_STORAGE_EVENT, handleTokenChange)
    }
  }, [])

  const login = useCallback(async (username: string, password: string) => {
    const response = await loginRequest(username, password)
    localStorage.setItem(TOKEN_STORAGE_KEY, response.token)
    window.dispatchEvent(new Event(AUTH_STORAGE_EVENT))
    setToken(response.token)

    return response
  }, [])

  const logout = useCallback(() => {
    localStorage.removeItem(TOKEN_STORAGE_KEY)
    window.dispatchEvent(new Event(AUTH_STORAGE_EVENT))
    setToken(null)
  }, [])

  const isAuthenticated = useMemo(() => token !== null && token.length > 0, [token])

  return {
    token,
    isAuthenticated,
    login,
    logout,
  }
}
