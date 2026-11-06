import preact from '@preact/preset-vite'
import { defineConfig } from 'vite'

// Required for future shared WebAssembly.Memory/Atomics workers.
const isolationHeaders = {
  'Cross-Origin-Opener-Policy': 'same-origin',
  'Cross-Origin-Embedder-Policy': 'require-corp',
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [preact()],
  // Includes the generated .NET bundle at public/wasm, copied unchanged into dist/wasm.
  publicDir: 'public',
  server: { headers: isolationHeaders },
  preview: { headers: isolationHeaders },
  // Keep the kernel as a fetchable asset even while the placeholder is tiny.
  build: { assetsInlineLimit: filePath => filePath.endsWith('.wasm') ? false : undefined },
})
