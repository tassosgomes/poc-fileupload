import { Navigate, Route, Routes } from 'react-router-dom'
import { Layout } from '@/components/Layout'
import { useAuth } from '@/features/auth/hooks/useAuth'
import { FileListTable } from '@/features/files/components/FileListTable'
import { LoginPage } from '@/features/auth/components/LoginPage'
import { MinioUploadPage } from '@/features/upload-minio/components/MinioUploadPage'
import { TusUploadPage } from '@/features/upload-tus/components/TusUploadPage'

function ProtectedLayout() {
  const { isAuthenticated } = useAuth()

  if (!isAuthenticated) {
    return <Navigate replace to="/login" />
  }

  return <Layout />
}

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<ProtectedLayout />} path="/">
        <Route element={<Navigate replace to="/upload/tus" />} index />
        <Route element={<TusUploadPage />} path="upload/tus" />
        <Route element={<MinioUploadPage />} path="upload/minio" />
        <Route element={<FileListTable />} path="files" />
      </Route>
      <Route path="*" element={<Navigate replace to="/" />} />
    </Routes>
  )
}
