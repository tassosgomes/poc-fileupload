import { ProgressBar } from '@/components/ProgressBar'
import { useTusUpload } from '@/features/upload-tus/hooks/useTusUpload'

export function TusUploadPage() {
  const { progress } = useTusUpload()

  return (
    <section>
      <h2>TUS Upload</h2>
      <p>Resumable upload flow scaffolded with tus-js-client dependency.</p>
      <ProgressBar progress={progress} />
    </section>
  )
}
