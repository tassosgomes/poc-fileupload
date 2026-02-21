import { useAuth } from '@/features/auth/hooks/useAuth'

export function LoginPage() {
  const { login } = useAuth()

  return (
    <section>
      <h2>Login</h2>
      <p>Scaffold ready for JWT authentication flow.</p>
      <button onClick={login} type="button">
        Simulate Login
      </button>
    </section>
  )
}
