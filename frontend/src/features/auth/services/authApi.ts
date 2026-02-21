import { apiClient } from '@/services/api'

export async function authenticate() {
  await apiClient.post('/api/v1/auth/login', {
    username: 'poc',
    password: 'poc123',
  })
}
