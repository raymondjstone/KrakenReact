import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      output: {
        // Large libraries in their own files: they change far less often than the app, so browsers keep them
        // cached across deploys, and they download in parallel with the app code.
        manualChunks: {
          'vendor-react': ['react', 'react-dom', 'react-router-dom'],
          'vendor-grid': ['ag-grid-community', 'ag-grid-react'],
          'vendor-charts': ['lightweight-charts'],
          'vendor-signalr': ['@microsoft/signalr'],
        },
      },
    },
  },
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
