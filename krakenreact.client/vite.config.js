import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: process.env.API_URL || 'https://localhost:7247',
        secure: false,
        changeOrigin: true
      },
      '/tradingHub': {
        target: process.env.API_URL || 'https://localhost:7247',
        secure: false,
        changeOrigin: true,
        ws: true
      }
    }
  }
})
