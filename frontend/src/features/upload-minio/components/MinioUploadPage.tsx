import { ProgressBar } from '@/components/ProgressBar'
import { useMinioUpload } from '@/features/upload-minio/hooks/useMinioUpload'

export function MinioUploadPage() {
  const { progress } = useMinioUpload()

  return (
    <section>
      <h2>MinIO Multipart Upload</h2>
      <p>Parallel multipart upload flow scaffolded for pre-signed URLs.</p>
      <ProgressBar progress={progress} />
    </section>
  )
}
