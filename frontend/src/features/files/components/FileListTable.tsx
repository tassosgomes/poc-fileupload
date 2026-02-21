import { useFiles } from '@/features/files/hooks/useFiles'

export function FileListTable() {
  const files = useFiles()

  return (
    <section>
      <h2>Uploaded Files</h2>
      <table>
        <thead>
          <tr>
            <th>Name</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.id}>
              <td>{file.fileName}</td>
              <td>{file.status}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
