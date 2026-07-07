import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { CopilotKit } from '@copilotkit/react-core/v2'
import '@copilotkit/react-core/v2/styles.css'
import './index.css'
import App from './App.tsx'

// Point the chat at a CopilotKit runtime. Configure via a Vite env var, e.g.
//   VITE_COPILOTKIT_RUNTIME_URL=http://localhost:4000/api/copilotkit
// or use Copilot Cloud with VITE_COPILOTKIT_PUBLIC_API_KEY.
const runtimeUrl =
  import.meta.env.VITE_COPILOTKIT_RUNTIME_URL ?? '/api/copilotkit'
const publicApiKey = import.meta.env.VITE_COPILOTKIT_PUBLIC_API_KEY

// `CopilotKit`'s `inspectorDefaultAnchor` prop is a no-op in the currently
// installed @copilotkit/web-inspector build (it never reads that prop) — the
// widget only ever gets its anchor from this localStorage entry on first
// hydrate. Seed it once so the inspector starts top-left; skip if it's
// already set so we don't fight a position the user has since dragged to.
const INSPECTOR_STORAGE_KEY = 'cpk:inspector:state'
if (!localStorage.getItem(INSPECTOR_STORAGE_KEY)) {
  const anchor = { horizontal: 'left' as const, vertical: 'top' as const }
  localStorage.setItem(
    INSPECTOR_STORAGE_KEY,
    JSON.stringify({
      button: { anchor, anchorOffset: { x: 16, y: 16 }, hasCustomPosition: false },
      window: {
        anchor,
        anchorOffset: { x: 16, y: 16 },
        size: { width: 600, height: 700 },
        hasCustomPosition: false,
      },
    }),
  )
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {/* useSingleEndpoint={false}: the runtime exposes the multi-route CopilotKit
        endpoint, so force REST transport (runtime-info + threads hit the same base). */}
    <CopilotKit
      runtimeUrl={runtimeUrl}
      publicApiKey={publicApiKey}
      useSingleEndpoint={false}
    >
      <App />
    </CopilotKit>
  </StrictMode>,
)
