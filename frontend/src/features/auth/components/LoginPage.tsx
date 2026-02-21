import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import axios from 'axios'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '@/features/auth/hooks/useAuth'

export function LoginPage() {
  const navigate = useNavigate()
  const { isAuthenticated, login } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [errorMessage, setErrorMessage] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)

  useEffect(() => {
    if (isAuthenticated) {
      navigate('/', { replace: true })
    }
  }, [isAuthenticated, navigate])

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setErrorMessage('')
    setIsSubmitting(true)

    try {
      await login(username, password)
      navigate('/', { replace: true })
    } catch (error: unknown) {
      if (axios.isAxiosError(error) && error.response?.status === 401) {
        setErrorMessage('Invalid username or password.')
      } else {
        setErrorMessage('Unable to login right now. Please try again.')
      }
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <section className="auth-card" aria-labelledby="login-title">
      <h2 id="login-title">Login</h2>
      <form className="auth-form" onSubmit={handleSubmit}>
        <label htmlFor="username">Username</label>
        <input
          autoComplete="username"
          id="username"
          onChange={(event) => setUsername(event.target.value)}
          required
          value={username}
        />

        <label htmlFor="password">Password</label>
        <input
          autoComplete="current-password"
          id="password"
          onChange={(event) => setPassword(event.target.value)}
          required
          type="password"
          value={password}
        />

        {errorMessage ? (
          <p aria-live="assertive" className="auth-error">
            {errorMessage}
          </p>
        ) : null}

        <button disabled={isSubmitting} type="submit">
          {isSubmitting ? 'Signing in...' : 'Login'}
        </button>
      </form>
    </section>
  )
}
