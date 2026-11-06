import kernelUrl from '../assets/rust-kernel.wasm?url'
import type { KernelExports } from '../../../rust-kernel/interface.mjs'

/** Each simulation worker owns an independent kernel instance and its mutable memory. */
export async function loadRustKernel(): Promise<KernelExports> {
  const response = await fetch(kernelUrl)
  if (!response.ok) throw new Error(`Rust kernel fetch failed: ${response.status}`)
  const { instance } = await WebAssembly.instantiateStreaming(response)
  return instance.exports as KernelExports
}
