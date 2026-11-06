// SPDX-License-Identifier: GPL-3.0-or-later

import { prepareKernel, kernelStatuses } from './interface.mjs'

const stackBytes = 65536
// Separate completion words by 64 bytes; only the command generation is shared.
const completion = index => (index + 1) * 16
const check = status => {
  if (status !== 0) throw new Error(`Rust kernel: ${kernelStatuses[status] ?? status}`)
}

/** Fixed bands over one imported shared memory. createWorker supplies a persistent
 * module worker with the usual postMessage/onmessage/onerror/terminate interface.
 * All memory growth and initialisation finish before compute workers are started.
 */
export async function createParallelKernel(module, prepared, { workerCount, createWorker, cancellation, timeoutMs = 30000 }) {
  const packet = prepared instanceof Uint8Array ? prepared : prepared.packet
  const header = new DataView(packet.buffer, packet.byteOffset, packet.byteLength)
  const height = header.getUint32(28, true)
  const substeps = header.getUint32(48, true)
  const probeCount = header.getUint32(72, true)
  const lastStep = header.getUint32(64, true)
  if (!Number.isInteger(workerCount) || workerCount < 1) throw new RangeError('Invalid compute worker count')
  const count = Math.min(workerCount, height)
  const memory = new WebAssembly.Memory({ initial: 32, maximum: 32768, shared: true })
  const exports = { ...(await WebAssembly.instantiate(module, { env: { memory } })).exports, memory }
  const heap = exports.kernel_heap_base()
  const run = prepareKernel(exports, packet, heap + count * stackBytes)
  const outputBytes = Math.max(header.getUint32(16, true) * header.getUint32(20, true) * 4,
    run.layout.pixelCount * 4, probeCount * 4)
  const end = run.outputAddress + outputBytes
  if (end > memory.buffer.byteLength) memory.grow(Math.ceil((end - memory.buffer.byteLength) / 65536))
  const control = new Int32Array(new SharedArrayBuffer((count + 1) * 64))
  // Each worker publishes its cumulative kernel time before its completion word.
  const workTimes = Array.from({ length: count }, (_, index) => new Float64Array(control.buffer, (completion(index) + 2) * 4, 1))
  const cancel = cancellation ?? new Int32Array(new SharedArrayBuffer(4))
  const workers = []
  const startupTimers = new Set()
  const args = [run.packageAddress, run.packageBytes, run.stateAddress, run.layout.stateBytes]
  let failure, closed = false, advancing = false, generation = 0
  let coordinatorMilliseconds = 0
  let workBaseline = workTimes.map(() => 0)
  function phase(operation, capacity) {
    const started = performance.now()
    try { check(exports.kernel_phase(...args, operation, run.outputAddress, capacity)) }
    finally { coordinatorMilliseconds += performance.now() - started }
  }
  function fail(error) {
    failure ??= error instanceof Error ? error : new Error(String(error))
    Atomics.store(control, 1, 1)
    Atomics.notify(control, 0)
  }
  function ensureActive() {
    if (failure) throw failure
    if (closed || Atomics.load(cancel, 0)) throw new Error('Simulation cancelled.')
    if (Atomics.load(control, 1)) throw new Error('A Rust compute worker failed.')
  }
  function close() {
    closed = true
    Atomics.store(control, 1, 1)
    Atomics.notify(control, 0)
    for (const timer of startupTimers) clearTimeout(timer)
    startupTimers.clear()
    for (const worker of workers) worker.terminate()
  }
  try {
    await Promise.all(Array.from({ length: count }, (_, index) => new Promise((resolve, reject) => {
      const worker = createWorker()
      workers.push(worker)
      const timer = setTimeout(() => { fail(new Error('Rust compute worker startup timed out.')); reject(failure) }, timeoutMs)
      startupTimers.add(timer)
      worker.onerror = event => {
        event.preventDefault?.()
        clearTimeout(timer)
        fail(new Error(event.message || 'Rust compute worker failed.'))
        reject(failure)
      }
      worker.onmessageerror = () => worker.onerror({ message: 'Could not read the Rust compute worker response.' })
      worker.onmessage = ({ data }) => {
        clearTimeout(timer)
        if (data.type === 'ready') resolve()
        else { fail(new Error(data.error || 'Rust compute worker failed.')); reject(failure) }
      }
      worker.postMessage({ module, memory, control, cancellation: cancel, index,
        stackTop: heap + (index + 1) * stackBytes, args,
        rowStart: Math.floor(index * height / count), rowEnd: Math.floor((index + 1) * height / count) })
    })))
    ensureActive()
  } catch (error) {
    close()
    throw error
  }
  async function barrier() {
    const deadline = performance.now() + timeoutMs
    await Promise.all(workers.map(async (_, index) => {
      const word = completion(index)
      while (Atomics.load(control, word) !== generation) {
        ensureActive()
        if (performance.now() > deadline) throw new Error(`Rust compute worker ${index} timed out.`)
        const previous = Atomics.load(control, word)
        if (previous === generation) break
        if (typeof Atomics.waitAsync === 'function') {
          await Atomics.waitAsync(control, word, previous, 100).value
        } else {
          await new Promise(resolve => setTimeout(resolve, 1))
        }
      }
      ensureActive()
      check(Atomics.load(control, word + 1))
    }))
  }
  return Object.freeze({ ...run, workerCount: count, close,
    timings() {
      if (advancing) throw new Error('Cannot read timings during an advance.')
      return { coordinatorMilliseconds: coordinatorMilliseconds + run.timings().coordinatorMilliseconds,
        workerMilliseconds: workTimes.map((value, index) => value[0] - workBaseline[index]) }
    },
    async advance(steps) {
      ensureActive()
      if (advancing) throw new Error('Concurrent simulation advances are not supported.')
      const completed = run.views().control[0]
      if (!Number.isInteger(steps) || steps < 0 || steps > lastStep - completed)
        throw new RangeError('Invalid simulation step count')
      if (steps * probeCount * 4 > 0x7fffffff) throw new RangeError('Simulation samples exceed signed 32-bit capacity')
      const samples = new Float32Array(steps * probeCount)
      advancing = true
      try {
        for (let step = 0; step < steps; step++) {
          ensureActive()
          phase(0, 0)
          for (let sub = 0; sub < substeps; sub++) {
            generation = (generation + 1) | 0
            Atomics.store(control, 0, generation)
            Atomics.notify(control, 0)
            await barrier()
            const size = sub + 1 === substeps ? probeCount * 4 : 0
            phase(1, size)
          }
          samples.set(new Float32Array(memory.buffer, run.outputAddress, probeCount), step * probeCount)
        }
        return samples
      } catch (error) {
        fail(error)
        close()
        throw error
      } finally {
        advancing = false
      }
    },
    reset() {
      ensureActive()
      if (advancing) throw new Error('Cannot reset during an advance.')
      run.reset()
      coordinatorMilliseconds = 0
      workBaseline = workTimes.map(value => value[0])
    },
    observe() { ensureActive(); if (advancing) throw new Error('Cannot observe during an advance.'); return run.observe() },
    capture() { ensureActive(); if (advancing) throw new Error('Cannot capture during an advance.'); return run.capture() },
  })
}

/** Run in a compute worker after receiving its immutable setup. No messages or
 * row claims occur per substep. Timed idle waits allow cancellation even if the
 * coordinator was terminated before it could publish a shutdown generation.
 */
export async function runComputeWorker(setup, postMessage) {
  const { module, memory, control, cancellation, index, stackTop, args, rowStart, rowEnd } = setup
  const word = completion(index)
  const workTime = new Float64Array(control.buffer, (word + 2) * 4, 1)
  let workMilliseconds = 0
  try {
    const { exports } = await WebAssembly.instantiate(module, { env: { memory } })
    exports.__stack_pointer.value = stackTop
    let seen = 0
    postMessage({ type: 'ready' })
    while (!Atomics.load(control, 1) && !Atomics.load(cancellation, 0)) {
      const generation = Atomics.load(control, 0)
      if (generation === seen) { Atomics.wait(control, 0, seen, 100); continue }
      const started = performance.now()
      const status = exports.kernel_step_band(...args, rowStart, rowEnd)
      workMilliseconds += performance.now() - started
      workTime[0] = workMilliseconds
      Atomics.store(control, word + 1, status)
      Atomics.store(control, word, generation)
      Atomics.notify(control, word)
      check(status)
      seen = generation
    }
  } catch (error) {
    Atomics.store(control, 1, 1)
    Atomics.notify(control, word)
    postMessage({ type: 'error', error: error instanceof Error ? error.message : String(error) })
  }
}
