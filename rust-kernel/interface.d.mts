// SPDX-License-Identifier: GPL-3.0-or-later

export interface KernelExports extends WebAssembly.Exports {
  memory: WebAssembly.Memory
  kernel_abi_version(): number
  kernel_capabilities(): number
  kernel_heap_base(): number
  kernel_plan(packageAddress: number, packageBytes: number, descriptorAddress: number, capacity: number): number
  kernel_initialise(packageAddress: number, packageBytes: number, stateAddress: number, capacity: number): number
  /** Operations 0 reset, 1 advance, 2 observe, 3 capture; requires a validated immutable package. */
  kernel_execute_prepared(packageAddress: number, packageBytes: number, stateAddress: number, stateCapacity: number,
    operation: number, steps: number, outputAddress: number, capacity: number): number
  kernel_advance(packageAddress: number, packageBytes: number, stateAddress: number, stateCapacity: number,
    steps: number, outputAddress: number, capacity: number): number
  kernel_observe(packageAddress: number, packageBytes: number, stateAddress: number, stateCapacity: number,
    outputAddress: number, capacity: number): number
  kernel_capture(packageAddress: number, packageBytes: number, stateAddress: number, stateCapacity: number,
    outputAddress: number, capacity: number): number
  kernel_step_band(packageAddress: number, packageBytes: number, stateAddress: number, stateCapacity: number,
    rowStart: number, rowEnd: number): number
  kernel_phase(packageAddress: number, packageBytes: number, stateAddress: number, stateCapacity: number,
    phase: number, outputAddress: number, capacity: number): number
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
  /** Scratch output begins here; numerical helpers grow memory and reuse this region. */
  readonly outputAddress: number
  readonly layout: KernelLayout
  timings(): { coordinatorMilliseconds: number, workerMilliseconds: number[] }
  reset(): void
  /** Step-major active-probe slots, zero for lines. Samples include cumulative point totals. */
  advance(steps: number): Float32Array<ArrayBuffer>
  /** Flattened probe-pixel order; integrated totals or live field/unrooted intensity. */
  observe(): Float32Array<ArrayBuffer>
  /** f16 field plane followed by f16 sqrt-intensity plane, little-endian. */
  capture(): Uint8Array<ArrayBuffer>
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
