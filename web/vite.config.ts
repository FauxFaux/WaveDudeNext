import preact from '@preact/preset-vite'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [preact()],
  // Includes the generated .NET bundle at public/wasm, copied unchanged into dist/wasm.
  publicDir: 'public',
})
