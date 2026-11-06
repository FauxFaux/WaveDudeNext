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
function build(shared) {
  const flags = shared ? [
    '-C', 'target-feature=+simd128,+atomics,+bulk-memory,+mutable-globals',
    '-C', 'link-arg=--import-memory', '-C', 'link-arg=--shared-memory',
    '-C', 'link-arg=--max-memory=2147483648', '-C', 'link-arg=--export=__stack_pointer',
  ] : ['-C', 'target-feature=+simd128']
  const result = spawnSync('cargo', [
    ...(shared ? ['+nightly'] : []),
    'build', '--locked', '--release', '--lib', '--package', 'rust-kernel',
    '--target', 'wasm32-unknown-unknown', '--target-dir', fileURLToPath(new URL(shared ? 'shared/' : './', target)),
    ...(shared ? ['-Z', 'build-std=std,panic_abort'] : []),
  ], { cwd: fileURLToPath(kernel), env: { ...env, CARGO_ENCODED_RUSTFLAGS: flags.join('\x1f') }, stdio: 'inherit' })

  if (result.error) throw result.error
  if (result.status !== 0) process.exit(result.status ?? 1)
}

const assets = new URL('../web/src/assets/', kernel)
await mkdir(assets, { recursive: true })
for (const shared of [false, true]) {
  build(shared)
  await copyFile(
    new URL(`${shared ? 'shared/' : ''}wasm32-unknown-unknown/release/rust_kernel.wasm`, target),
    new URL(shared ? 'rust-kernel-shared.wasm' : 'rust-kernel.wasm', assets),
  )
}
console.log('Built ordinary and shared Rust kernels in web/src/assets/')
