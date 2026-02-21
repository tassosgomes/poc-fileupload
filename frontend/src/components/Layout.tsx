import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useAuth } from '@/features/auth/hooks/useAuth'

export function Layout() {
  const navigate = useNavigate()
  const { logout } = useAuth()

  const handleLogout = () => {
    logout()
    navigate('/login', { replace: true })
  }

  return (
    <div className="app-shell">
      <header className="app-header">
        <Link className="app-title" to="/upload/tus">
          Upload POC
        </Link>

        <nav className="app-nav">
          <NavLink to="/upload/tus">Upload TUS</NavLink>
          <NavLink to="/upload/minio">Upload MinIO</NavLink>
          <NavLink to="/files">Arquivos</NavLink>
        </nav>

        <button onClick={handleLogout} type="button">
          Logout
        </button>
      </header>

      <main>
        <Outlet />
      </main>
    </div>
  )
}
