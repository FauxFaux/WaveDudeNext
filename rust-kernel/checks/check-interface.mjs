import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { prepareKernel } from '../interface.mjs'
import { decodePreparedSimulation } from '../../web/src/Wasm/PreparedSimulation.ts'

const wasm = await readFile(new URL('../../web/src/assets/rust-kernel.wasm', import.meta.url))
const packet = new Uint8Array(await readFile(process.argv[2] ?? '/tmp/wavedude-prepare/Example01_Source_Mirror_Probe.wdprep'))
const module = await WebAssembly.compile(wasm)
const { exports: kernel } = await WebAssembly.instantiate(module)
assert.equal(kernel.kernel_abi_version(), 1)
assert.equal(kernel.kernel_capabilities(), 63, 'Planning, initialisation, stepping, probes, capture and prepared execution are available')
assert.equal(typeof kernel.kernel_advance, 'function')
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
// Numerical exports apply the same checked arena contract, including byte
// capacities, disjoint output regions and no partial writes on rejected batches.
run.reset()
run.capture() // Reserve the largest output before acquiring these views.
const args = [run.packageAddress, packet.byteLength, run.stateAddress, run.layout.stateBytes]
const output = run.outputAddress
const sampleBytes = prepared.probes.length * 4
const frameBytes = prepared.width * prepared.height * 4
const unchanged = (call, status) => {
  const state = new Uint8Array(kernel.memory.buffer, run.stateAddress, run.layout.stateBytes)
  const scratch = new Uint8Array(kernel.memory.buffer, output, frameBytes)
  const before = state.slice()
  scratch.fill(0xab)
  assert.equal(call(), status)
  assert.deepEqual(state, before, 'Rejected numerical calls preserve the complete state')
  assert.ok(scratch.every(v => v === 0xab), 'Rejected numerical calls preserve output')
}
unchanged(() => kernel.kernel_advance(...args, 1, output, sampleBytes - 1), 6)
unchanged(() => kernel.kernel_advance(...args, prepared.totalSteps + 1, output, frameBytes), 9)
unchanged(() => kernel.kernel_capture(...args, output, frameBytes - 1), 6)
unchanged(() => kernel.kernel_observe(...args, output, prepared.probes.reduce((sum, p) => sum + p.pixels.length, 0) * 4 - 1), 6)
unchanged(() => kernel.kernel_capture(...args, output + 1, frameBytes - 1), 1)
unchanged(() => kernel.kernel_capture(...args, run.stateAddress, frameBytes), 7)
unchanged(() => kernel.kernel_advance(...args, 1, base, sampleBytes), 7)
// Prepared execution keeps the same range/state/capacity checks without scanning
// immutable values again. Every rejection must preserve state and output.
const execute = (operation, steps, address = output, capacity = frameBytes, region = args) =>
  kernel.kernel_execute_prepared(...region, operation, steps, address, capacity)
unchanged(() => execute(4, 0), 5)
unchanged(() => execute(0, 1), 5)
unchanged(() => execute(2, 1), 5)
unchanged(() => execute(3, 1), 5)
unchanged(() => execute(1, 1, output, sampleBytes - 1), 6)
unchanged(() => execute(1, prepared.totalSteps + 1), 9)
unchanged(() => execute(2, 0, output, run.layout.pixelCount * 4 - 1), 6)
unchanged(() => execute(3, 0, output, frameBytes - 1), 6)
unchanged(() => execute(3, 0, output + 1, frameBytes - 1), 1)
unchanged(() => execute(3, 0, run.stateAddress), 7)
unchanged(() => execute(1, 1, base, sampleBytes), 7)
unchanged(() => execute(0, 0, output, 0, [...args.slice(0, 3), run.layout.stateBytes - 1]), 6)
unchanged(() => execute(3, 0, output, frameBytes, [base + 1, ...args.slice(1)]), 1)
run.views().control[1] = 2
unchanged(() => kernel.kernel_advance(...args, 1, output, sampleBytes), 9)
unchanged(() => kernel.kernel_capture(...args, output, frameBytes), 9)
unchanged(() => execute(1, 1), 9)
unchanged(() => execute(2, 0), 9)
unchanged(() => execute(3, 0), 9)
run.views().control[1] = 0
unchanged(() => kernel.kernel_advance(...args, 0, output, frameBytes), 0)
unchanged(() => kernel.kernel_phase(...args, 2, output, 0), 5)
unchanged(() => kernel.kernel_phase(...args, 1, output, sampleBytes), 9)
unchanged(() => kernel.kernel_step_band(...args, 0, prepared.gridHeight), 9)
unchanged(() => kernel.kernel_step_band(...args, 0, prepared.gridHeight + 1), 1)
assert.equal(kernel.kernel_phase(...args, 0, output, 0), 0)
unchanged(() => kernel.kernel_phase(...args, 0, output, 0), 9)
unchanged(() => kernel.kernel_advance(...args, 1, output, sampleBytes), 9)
unchanged(() => execute(2, 0), 9)
unchanged(() => execute(3, 0), 9)
for (let sub = 0; sub < prepared.subSteps; sub++) {
  const middle = Math.floor(prepared.gridHeight / 2)
  assert.equal(kernel.kernel_step_band(...args, 0, middle), 0)
  assert.equal(kernel.kernel_step_band(...args, middle, prepared.gridHeight), 0)
  if (sub + 1 === prepared.subSteps) unchanged(() => kernel.kernel_phase(...args, 1, output, sampleBytes - 1), 6)
  assert.equal(kernel.kernel_phase(...args, 1, output, sampleBytes), 0)
}
assert.deepEqual(Array.from(run.views().control).slice(2), [0, 0])
run.reset()
const scratch = new Uint8Array(kernel.memory.buffer, output, frameBytes)
scratch.fill(0xab)
assert.equal(kernel.kernel_advance(...args, 1, output, frameBytes), 0)
assert.ok(scratch.subarray(sampleBytes).every(v => v === 0xab), 'Unused output capacity is untouched')
assert.equal(run.views().control[0], 1)
const stateBefore = new Uint8Array(kernel.memory.buffer, run.stateAddress, run.layout.stateBytes).slice()
const ownedFrame = run.capture()
run.observe()
assert.deepEqual(new Uint8Array(kernel.memory.buffer, run.stateAddress, run.layout.stateBytes), stateBefore,
  'Capture and observations never change the filters or integrated totals')
run.advance(2)
assert.equal(ownedFrame.length, frameBytes, 'Owned outputs survive later scratch reuse')
assert.throws(() => run.advance(-1), RangeError)
assert.throws(() => run.advance(.5), RangeError)

// Compare prepared execution with the fully validating ABI on independent arenas,
// including complete working state, live/integrated observations and frame bytes.
run.reset()
assert.equal(other.kernel_initialise(second.packageAddress, packet.byteLength, second.stateAddress, second.layout.stateBytes), 0)
second.capture() // Reserve scratch before raw calls.
const strictArgs = [second.packageAddress, packet.byteLength, second.stateAddress, second.layout.stateBytes]
for (const steps of [1, 2, 4]) {
  const samples = run.advance(steps)
  assert.equal(other.kernel_advance(...strictArgs, steps, second.outputAddress, samples.byteLength), 0)
  assert.deepEqual(samples, new Float32Array(other.memory.buffer, second.outputAddress, samples.length))
  assert.deepEqual(new Uint8Array(kernel.memory.buffer, run.stateAddress, run.layout.stateBytes),
    new Uint8Array(other.memory.buffer, second.stateAddress, second.layout.stateBytes))
  assert.equal(other.kernel_observe(...strictArgs, second.outputAddress, second.layout.pixelCount * 4), 0)
  assert.deepEqual(run.observe(), new Float32Array(other.memory.buffer, second.outputAddress, second.layout.pixelCount))
  assert.equal(other.kernel_capture(...strictArgs, second.outputAddress, frameBytes), 0)
  assert.deepEqual(run.capture(), new Uint8Array(other.memory.buffer, second.outputAddress, frameBytes))
}

// The JS run must perform exactly one full package-validation call at upload,
// including across resets and memory growth. Legacy numerical exports must not
// accidentally reappear on the application path.
const { exports: tracked } = await WebAssembly.instantiate(module)
let plans = 0, executions = 0
const trackedExports = { ...tracked,
  kernel_plan(...args) { plans++; return tracked.kernel_plan(...args) },
  kernel_execute_prepared(...args) { executions++; return tracked.kernel_execute_prepared(...args) },
}
for (const name of ['kernel_initialise', 'kernel_advance', 'kernel_observe', 'kernel_capture'])
  trackedExports[name] = () => { throw new Error(`Repeated package validation through ${name}`) }
const trackedRun = prepareKernel(trackedExports, packet)
trackedRun.advance(1)
trackedRun.observe()
trackedRun.capture()
tracked.memory.grow(1)
trackedRun.reset()
trackedRun.advance(2)
trackedRun.observe()
trackedRun.capture()
assert.equal(plans, 1)
assert.equal(executions, 8)
const invalid = packet.slice()
new DataView(invalid.buffer).setUint32(prepared.offsets.c2, 0x7fc00000, true)
assert.throws(() => prepareKernel(trackedExports, invalid), /invalid numeric value/,
  'New or modified packages must pass full validation before prepared execution')
console.log(`Rust preparation interface passed against ${packet.byteLength} C# package bytes (${run.layout.cells} cells, ${run.layout.stateBytes} state bytes).`)
