// SPDX-License-Identifier: GPL-3.0-or-later

/** Status names for the numeric Wasm ABI. */
export const kernelStatuses = Object.freeze([
  'ok', 'invalid memory range/alignment', 'invalid package', 'unsupported package version',
  'invalid package layout', 'invalid numeric value', 'insufficient capacity', 'overlapping regions', 'size overflow',
  'invalid state or step range',
])

function check(status) {
  if (status !== 0) throw new Error(`Rust kernel: ${kernelStatuses[status] ?? `unknown status ${status}`}`)
}

const align = value => Math.ceil(value / 16) * 16

/**
 * Copy immutable WDPK bytes and initialise caller-owned state in one Wasm instance.
 * base defaults to the beginning of the JS-owned arena. Numerical calls use scratch
 * immediately after state; reserve that output space too when sharing an instance.
 * Reusing an arena invalidates its previous package/state views. Reserve disjoint
 * arenas if multiple prepared runs must coexist; do not call while workers use it.
 */
export function prepareKernel(exports, prepared, base = exports.kernel_heap_base()) {
  if (exports.kernel_abi_version() !== 1 || (exports.kernel_capabilities() & 3) !== 3)
    throw new Error('Rust kernel does not support the preparation interface')
  const packageBytes = prepared instanceof Uint8Array ? prepared : prepared?.packet
  if (!(packageBytes instanceof Uint8Array)) throw new TypeError('Expected PreparedSimulation or its WDPK Uint8Array packet')
  if (!Number.isInteger(base) || base < exports.kernel_heap_base() || base % 16 !== 0)
    throw new RangeError('Kernel arena base must be 16-byte aligned and above kernel_heap_base')
  const memory = exports.memory
  // Growth detaches ordinary memory views. Preserve an input already in this memory.
  const packet = packageBytes.buffer === memory.buffer ? packageBytes.slice() : packageBytes
  const ensure = end => {
    if (!Number.isSafeInteger(end) || end > 0xffffffff) throw new RangeError('Kernel arena exceeds wasm32 memory')
    if (end > memory.buffer.byteLength) memory.grow(Math.ceil((end - memory.buffer.byteLength) / 65536))
  }
  const packageAddress = base
  const descriptorAddress = align(base + packet.byteLength)
  ensure(descriptorAddress + 64)
  new Uint8Array(memory.buffer, packageAddress, packet.byteLength).set(packet)
  check(exports.kernel_plan(packageAddress, packet.byteLength, descriptorAddress, 64))
  const descriptor = new DataView(memory.buffer, descriptorAddress, 64)
  if (descriptor.getUint32(0, true) !== 0x59414c4b || descriptor.getUint32(4, true) !== 1
      || descriptor.getUint32(8, true) !== 64 || descriptor.getUint32(16, true) !== 16)
    throw new Error('Unsupported Rust kernel state layout')
  const names = ['field0', 'field1', 'intensityStage1', 'intensityStage2', 'intensityOutput', 'emitterState', 'probeTotals']
  const layout = Object.freeze({ stateBytes: descriptor.getUint32(12, true),
    ...Object.fromEntries(names.map((name, index) => [name, descriptor.getUint32(20 + index * 4, true)])),
    cells: descriptor.getUint32(48, true), emitterCount: descriptor.getUint32(52, true), pixelCount: descriptor.getUint32(56, true) })
  const stateAddress = align(descriptorAddress + 64)
  ensure(stateAddress + layout.stateBytes)
  check(exports.kernel_initialise(packageAddress, packet.byteLength, stateAddress, layout.stateBytes))
  const header = new DataView(packet.buffer, packet.byteOffset, packet.byteLength)
  const probeCount = header.getUint32(72, true)
  const frameBytes = header.getUint32(16, true) * header.getUint32(20, true) * 4
  const outputAddress = align(stateAddress + layout.stateBytes)
  const args = [packageAddress, packet.byteLength, stateAddress, layout.stateBytes]
  const numerical = (name, size, extra = []) => {
    if ((exports.kernel_capabilities() & 28) !== 28) throw new Error('Rust kernel does not support numerical operations')
    if (!Number.isSafeInteger(size) || size > 0x7fffffff) throw new RangeError('Kernel output exceeds signed 32-bit capacity')
    ensure(outputAddress + size)
    check(exports[name](...args, ...extra, outputAddress, size))
  }
  return Object.freeze({ packageAddress, packageBytes: packet.byteLength, descriptorAddress, stateAddress, outputAddress, layout,
    reset() { check(exports.kernel_initialise(...args)) },
    /** Complete steps; step-major active-probe slots, zero for lines. Returned data is owned by JS. */
    advance(steps) {
      if (!Number.isInteger(steps) || steps < 0 || steps > 0xffffffff) throw new RangeError('Expected an unsigned step count')
      const size = steps * probeCount * 4
      numerical('kernel_advance', size, [steps])
      return new Float32Array(memory.buffer, outputAddress, steps * probeCount).slice()
    },
    /** Current per-pixel live values or integrated totals in prepared pixel-table order. */
    observe() {
      numerical('kernel_observe', layout.pixelCount * 4)
      return new Float32Array(memory.buffer, outputAddress, layout.pixelCount).slice()
    },
    /** Tightly packed f16 field followed by f16 sqrt-intensity; the rim/ring are excluded. */
    capture() {
      numerical('kernel_capture', frameBytes)
      return new Uint8Array(memory.buffer, outputAddress, frameBytes).slice()
    },
    // Call again after memory growth; never retain these views across a grow.
    views() {
      const buffer = memory.buffer
      const floats = offset => new Float32Array(buffer, stateAddress + offset, layout.cells)
      return {
        control: new Uint32Array(buffer, stateAddress, 4),
        field0: floats(layout.field0), field1: floats(layout.field1),
        intensityStage1: floats(layout.intensityStage1), intensityStage2: floats(layout.intensityStage2),
        intensityOutput: floats(layout.intensityOutput),
        emitterState: new DataView(buffer, stateAddress + layout.emitterState, layout.emitterCount * 16),
        probeTotals: new Float64Array(buffer, stateAddress + layout.probeTotals, layout.pixelCount),
      }
    },
  })
}
