import kernelUrl from '../assets/rust-kernel.wasm?url'
import sharedKernelUrl from '../assets/rust-kernel-shared.wasm?url'
import { prepareKernel } from '../../../rust-kernel/interface.mjs'
import { createParallelKernel } from '../../../rust-kernel/parallel.mjs'
import type { KernelExports } from '../../../rust-kernel/interface.mjs'
import type { PreparedSimulation } from './PreparedSimulation'

/** Each simulation worker owns an independent kernel instance and its mutable memory. */
export async function loadRustKernel(): Promise<KernelExports> {
  const response = await fetch(kernelUrl)
  if (!response.ok) throw new Error(`Rust kernel fetch failed: ${response.status}`)
  const { instance } = await WebAssembly.instantiateStreaming(response)
  return instance.exports as KernelExports
}

/** Reserve a core for the UI/coordinator and use at most four compute workers with fixed row stripes.
 * Ordinary memory remains available when cross-origin isolation is unavailable.
 */
export async function loadRustSimulationKernel(prepared: PreparedSimulation, cancellation?: Int32Array<SharedArrayBuffer>) {
  const workerCount = Math.min(4, Math.max(1, (navigator.hardwareConcurrency ?? 2) - 1), prepared.gridHeight)
  if (!globalThis.crossOriginIsolated || typeof SharedArrayBuffer === 'undefined' || workerCount === 1)
    return prepareKernel(await loadRustKernel(), prepared)
  const response = await fetch(sharedKernelUrl)
  if (!response.ok) throw new Error(`Rust shared kernel fetch failed: ${response.status}`)
  const module = await WebAssembly.compileStreaming(response)
  return createParallelKernel(module, prepared, { workerCount, cancellation,
    createWorker: index => new Worker(new URL('./RustCompute.worker.ts', import.meta.url), { type: 'module', name: `Simulation worker ${index + 1}` }) })
}
