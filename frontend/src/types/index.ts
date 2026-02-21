export type UploadStatus =
  | 'Pending'
  | 'Completed'
  | 'Corrupted'
  | 'Cancelled'
  | 'Failed'

export interface UploadDto {
  id: string
  fileName: string
  fileSizeBytes: number
  contentType: string
  expectedSha256: string
  actualSha256: string | null
  uploadScenario: string
  storageKey: string | null
  status: UploadStatus
  createdBy: string
  createdAt: string
  completedAt: string | null
}

export interface LoginRequest {
  username: string
  password: string
}

export interface LoginResponse {
  token: string
  expiresAt: string
}

export interface InitiateMinioResponse {
  uploadId: string
  storageKey: string
  presignedUrls: string[]
  partSizeBytes: number
  totalParts: number
}

export interface CompleteMinioRequest {
  uploadId: string
  parts: PartETag[]
}

export interface PartETag {
  partNumber: number
  eTag: string
}
