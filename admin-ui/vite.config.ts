import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // VITE_BASE_PATH='/auth/' for the Windows/IIS deployment (IAM runs under /auth/).
  // Docker deployment uses the default '/'.
  base: process.env.VITE_BASE_PATH ?? '/',
  build: {
    // On Windows, Rollup/rolldown rejects the absolute path that Vite resolves
    // as the HTML emitFile fileName. An explicit relative input avoids that.
    rollupOptions: { input: 'index.html' },
  },
})
