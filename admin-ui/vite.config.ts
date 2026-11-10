import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  base: '/auth/',
  build: {
    // On Windows, Rollup/rolldown rejects the absolute path that Vite resolves
    // as the HTML emitFile fileName. An explicit relative input avoids that.
    rollupOptions: { input: 'index.html' },
  },
})
