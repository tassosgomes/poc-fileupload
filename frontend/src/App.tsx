import { Navigate, Route, Routes } from 'react-router-dom'
import { Layout } from '@/components/Layout'
import { FileListTable } from '@/features/files/components/FileListTable'
import { LoginPage } from '@/features/auth/components/LoginPage'
import { MinioUploadPage } from '@/features/upload-minio/components/MinioUploadPage'
import { TusUploadPage } from '@/features/upload-tus/components/TusUploadPage'

export function App() {
  return (
    <Layout>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/upload/tus" element={<TusUploadPage />} />
        <Route path="/upload/minio" element={<MinioUploadPage />} />
        <Route path="/files" element={<FileListTable />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    </Layout>
  )
}
