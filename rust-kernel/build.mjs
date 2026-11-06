import { spawnSync } from 'node:child_process'
import { copyFile, mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'

const kernel = new URL('./', import.meta.url)
const target = new URL('../target/', kernel)
// Host-wide flags (for example target-cpu=native) override Cargo target flags.
// Use this kernel's target configuration for the browser build.
const env = { ...process.env }
delete env.RUSTFLAGS
delete env.CARGO_ENCODED_RUSTFLAGS
const result = spawnSync('cargo', [
  'build', '--locked', '--release', '--lib', '--package', 'rust-kernel',
  '--target', 'wasm32-unknown-unknown', '--target-dir', fileURLToPath(target),
], { cwd: fileURLToPath(kernel), env, stdio: 'inherit' })

if (result.error) throw result.error
if (result.status !== 0) process.exit(result.status ?? 1)

const assets = new URL('../web/src/assets/', kernel)
await mkdir(assets, { recursive: true })
await copyFile(
  new URL('wasm32-unknown-unknown/release/rust_kernel.wasm', target),
  new URL('rust-kernel.wasm', assets),
)
console.log('Built web/src/assets/rust-kernel.wasm')
