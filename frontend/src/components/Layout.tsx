import { Link } from 'react-router-dom'
import type { PropsWithChildren } from 'react'

export function Layout({ children }: PropsWithChildren) {
  return (
    <div className="app-shell">
      <header className="app-header">
        <h1>Upload POC</h1>
        <nav>
          <Link to="/login">Login</Link>
          <Link to="/upload/tus">Upload TUS</Link>
          <Link to="/upload/minio">Upload MinIO</Link>
          <Link to="/files">Files</Link>
        </nav>
      </header>
      <main>{children}</main>
    </div>
  )
}
