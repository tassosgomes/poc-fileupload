import { apiClient } from '@/services/api'
import type { LoginRequest, LoginResponse } from '@/types'

export async function login(
  username: string,
  password: string,
): Promise<LoginResponse> {
  const request: LoginRequest = { username, password }
  const response = await apiClient.post<LoginResponse>('/v1/auth/login', request)

  return response.data
}
