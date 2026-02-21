import axios from 'axios'
import { API_URL } from '@/config/runtimeConfig'

export const apiClient = axios.create({
  baseURL: API_URL,
  timeout: 30000,
})
