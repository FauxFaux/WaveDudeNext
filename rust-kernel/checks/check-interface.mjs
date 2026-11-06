import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { prepareKernel } from '../interface.mjs'
import { decodePreparedSimulation } from '../../web/src/Wasm/PreparedSimulation.ts'

const wasm = await readFile(new URL('../../web/src/assets/rust-kernel.wasm', import.meta.url))
const packet = new Uint8Array(await readFile(process.argv[2] ?? '/tmp/wavedude-prepare/Example01_Source_Mirror_Probe.wdprep'))
const module = await WebAssembly.compile(wasm)
const { exports: kernel } = await WebAssembly.instantiate(module)
assert.equal(kernel.kernel_abi_version(), 1)
assert.equal(kernel.kernel_capabilities(), 3, 'Initialisation is available; stepping is explicitly unavailable')
assert.equal(kernel.kernel_step_rows, undefined, 'There is no placeholder numerical implementation')
const base = kernel.kernel_heap_base() + 64
const before = packet.slice()
const prepared = decodePreparedSimulation(packet)
const run = prepareKernel(kernel, prepared, base)
assert.deepEqual(packet, before, 'Uploading never changes the C# packet')
assert.equal(run.packageAddress, base)
const source = new DataView(packet.buffer, packet.byteOffset, packet.byteLength)
const word = at => source.getUint32(at, true)
assert.equal(run.layout.cells, word(40))
assert.equal(run.layout.cells, prepared.cellCount)
assert.equal(run.layout.emitterCount, word(68))
assert.equal(run.layout.pixelCount, word(76))
assert.deepEqual(new Uint8Array(kernel.memory.buffer, base, packet.byteLength), packet, 'Input can be read at its relocated package base')
assert.deepEqual(new Float32Array(kernel.memory.buffer, base + prepared.offsets.c2, prepared.cellCount), prepared.c2)
assert.deepEqual(new Float32Array(kernel.memory.buffer, base + prepared.offsets.damping, prepared.cellCount), prepared.damping)
const views = run.views()
for (const field of [views.field0, views.field1, views.intensityStage1, views.intensityStage2, views.intensityOutput, views.probeTotals])
  assert.ok(field.every(value => value === 0), 'Every numerical working buffer starts clear')
assert.deepEqual(Array.from(views.control), [0, 0, 0, 0])
for (let i = 0; i < run.layout.emitterCount; i++) {
  const from = word(88) + i * 64
  assert.equal(views.emitterState.getFloat64(i * 16, true), 0)
  assert.equal(views.emitterState.getInt32(i * 16 + 8, true), source.getInt32(from + 20, true))
  assert.equal(views.emitterState.getUint32(i * 16 + 12, true), source.getUint32(from + 12, true))
}
views.field0[0] = 99
views.control[0] = 17
views.control[1] = 1
assert.equal(kernel.kernel_initialise(base, packet.byteLength, run.stateAddress, run.layout.stateBytes), 0)
assert.equal(views.field0[0], 0)
assert.deepEqual(Array.from(views.control), [0, 0, 0, 0], 'Reset returns step and field selection to zero')

// Invalid callers must receive status codes, not traps, writes into the packet,
// or silent partial initialisation. Keep state/descriptor sentinels intact.
new Uint8Array(kernel.memory.buffer, run.stateAddress, run.layout.stateBytes).fill(0xab)
assert.equal(kernel.kernel_initialise(base, packet.byteLength, run.stateAddress, run.layout.stateBytes - 1), 6)
assert.ok(new Uint8Array(kernel.memory.buffer, run.stateAddress, run.layout.stateBytes).every(v => v === 0xab))
assert.equal(kernel.kernel_plan(base, packet.byteLength, run.descriptorAddress, 63), 6)
assert.equal(kernel.kernel_plan(base + 1, packet.byteLength, run.descriptorAddress, 64), 1)
assert.equal(kernel.kernel_plan(0, packet.byteLength, run.descriptorAddress, 64), 1)
assert.equal(kernel.kernel_plan(0xfffffff0, 64, run.descriptorAddress, 64), 1)
assert.equal(kernel.kernel_plan(base, packet.byteLength, base, 64), 7)
assert.equal(kernel.kernel_initialise(base, packet.byteLength, base, packet.byteLength), 7)
for (const [offset, value, status] of [[4, 2, 3], [40, 0xffffffff, 4], [68, 0xffffffff, 8],
  [80, 161, 4], [84, 160, 4], [108, 0x7fc00000, 5], [word(88) + 12, 0, 5], [word(96), 0, 5]]) {
  const live = new Uint8Array(kernel.memory.buffer, base, packet.byteLength)
  live.set(packet)
  new DataView(kernel.memory.buffer).setUint32(base + offset, value, true)
  const descriptor = new Uint8Array(kernel.memory.buffer, run.descriptorAddress, 64)
  descriptor.fill(0xcd)
  assert.equal(kernel.kernel_plan(base, packet.byteLength, run.descriptorAddress, 64), status, `Bad input at ${offset}`)
  assert.ok(descriptor.every(v => v === 0xcd), 'Rejected inputs leave output descriptors untouched')
}
new Uint8Array(kernel.memory.buffer, base, packet.byteLength).set(packet)
const old = run.views().field0
kernel.memory.grow(1)
assert.equal(old.byteLength, 0, 'Ordinary views detach when memory grows')
assert.equal(run.views().field0.length, run.layout.cells, 'Fresh views recover after memory growth')
const { exports: other } = await WebAssembly.instantiate(module)
run.views().field0[0] = 456
const second = prepareKernel(other, packet)
second.views().field0[0] = 123
assert.equal(run.views().field0[0], 456, 'Separate instances have independent working state')
assert.deepEqual(new Uint8Array(kernel.memory.buffer, base, packet.byteLength), packet)
console.log(`Rust preparation interface passed against ${packet.byteLength} C# package bytes (${run.layout.cells} cells, ${run.layout.stateBytes} state bytes).`)
