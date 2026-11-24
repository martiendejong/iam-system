import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { registerServiceWorker } from './services/pushClient'

// Makes the site installable as an app and lets sign-in notifications arrive (no caching, see public/sw.js).
if (import.meta.env.PROD) void registerServiceWorker()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
