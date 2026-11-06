// SPDX-License-Identifier: GPL-3.0-or-later
import type { PreparedKernel } from './interface.mjs'

export interface ParallelKernel extends Omit<PreparedKernel, 'advance'> {
  readonly workerCount: number
  advance(steps: number): Promise<Float32Array<ArrayBuffer>>
  close(): void
}
export function createParallelKernel(module: WebAssembly.Module, prepared: { readonly packet: Uint8Array } | Uint8Array,
  options: { workerCount: number, createWorker: () => Worker, cancellation?: Int32Array<SharedArrayBuffer>, timeoutMs?: number }): Promise<ParallelKernel>
export function runComputeWorker(setup: unknown, postMessage: (message: unknown) => void): Promise<void>
