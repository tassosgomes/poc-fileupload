type RuntimeEnvironment = {
  API_URL?: string
}

declare global {
  interface Window {
    RUNTIME_ENV?: RuntimeEnvironment
  }
}

const runtimeEnv: RuntimeEnvironment =
  typeof window !== 'undefined' && window.RUNTIME_ENV ? window.RUNTIME_ENV : {}

export const API_URL = runtimeEnv.API_URL ?? '/api'
