export interface FileItem {
  id: string
  name: string
  status: 'PENDING' | 'COMPLETED' | 'CORRUPTED' | 'CANCELLED' | 'FAILED'
}
