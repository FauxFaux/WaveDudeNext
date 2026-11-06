// SPDX-License-Identifier: GPL-3.0-or-later

export interface KernelExports extends WebAssembly.Exports {
  memory: WebAssembly.Memory
  kernel_abi_version(): number
  kernel_capabilities(): number
  kernel_heap_base(): number
  kernel_plan(packageAddress: number, packageBytes: number, descriptorAddress: number, capacity: number): number
  kernel_initialise(packageAddress: number, packageBytes: number, stateAddress: number, capacity: number): number
}

/** Offsets in bytes relative to stateAddress, except counts and stateBytes. */
export interface KernelLayout {
  readonly stateBytes: number
  readonly field0: number
  readonly field1: number
  readonly intensityStage1: number
  readonly intensityStage2: number
  readonly intensityOutput: number
  readonly emitterState: number
  readonly probeTotals: number
  readonly cells: number
  readonly emitterCount: number
  readonly pixelCount: number
}

export interface PreparedKernel {
  readonly packageAddress: number
  readonly packageBytes: number
  readonly descriptorAddress: number
  readonly stateAddress: number
  readonly layout: KernelLayout
  /** Acquire fresh views after any memory growth; prior arenas may have been reused. */
  views(): {
    control: Uint32Array
    field0: Float32Array
    field1: Float32Array
    intensityStage1: Float32Array
    intensityStage2: Float32Array
    intensityOutput: Float32Array
    emitterState: DataView
    probeTotals: Float64Array
  }
}

export const kernelStatuses: readonly string[]
/** The JS model should retain the original C# bytes alongside its decoded fields. */
export function prepareKernel(exports: KernelExports, prepared: { readonly packet: Uint8Array } | Uint8Array, base?: number): PreparedKernel
