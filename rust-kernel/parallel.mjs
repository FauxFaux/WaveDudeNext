// SPDX-License-Identifier: GPL-3.0-or-later

import { prepareKernel, kernelStatuses } from './interface.mjs'

const stackBytes = 65536
// Separate completion words by 64 bytes; only the command generation is shared.
const completion = index => (index + 1) * 16
const check = status => {
  if (status !== 0) throw new Error(`Rust kernel: ${kernelStatuses[status] ?? status}`)
}

/** Fixed round-robin row stripes over one imported shared memory. createWorker supplies a persistent
 * module worker with the usual postMessage/onmessage/onerror/terminate interface.
 * All memory growth and initialisation finish before compute workers are started.
 */
export async function createParallelKernel(module, prepared, { workerCount, createWorker, cancellation, timeoutMs = 30000, rowsPerStripe = 16 }) {
  const packet = prepared instanceof Uint8Array ? prepared : prepared.packet
  const header = new DataView(packet.buffer, packet.byteOffset, packet.byteLength)
  const height = header.getUint32(28, true)
  const substeps = header.getUint32(48, true)
  const probeCount = header.getUint32(72, true)
  const lastStep = header.getUint32(64, true)
  if (!Number.isInteger(workerCount) || workerCount < 1) throw new RangeError('Invalid compute worker count')
  if (!Number.isInteger(rowsPerStripe) || rowsPerStripe < 0) throw new RangeError('Invalid row stripe size')
  const count = Math.min(workerCount, height)
  // Tiny grids still give every worker some rows. Zero selects contiguous quarters
  // for comparison; all other assignments are fixed for the run, without a queue.
  const stripeRows = Math.min(rowsPerStripe, Math.max(1, Math.floor(height / count)))
  const memory = new WebAssembly.Memory({ initial: 32, maximum: 32768, shared: true })
  const exports = { ...(await WebAssembly.instantiate(module, { env: { memory } })).exports, memory }
  if (typeof exports.kernel_step_stripes !== 'function') throw new Error('Rust kernel needs rebuilding for stripe scheduling')
  const heap = exports.kernel_heap_base()
  const run = prepareKernel(exports, packet, heap + count * stackBytes)
  // Derive metadata only after validating the packet's dimensions and maps.
  const bands = Array.from({ length: count }, (_, index) => {
    if (stripeRows === 0) return [[Math.floor(index * height / count), Math.floor((index + 1) * height / count)]]
    const ranges = []
    for (let start = index * stripeRows; start < height; start += count * stripeRows)
      ranges.push([start, Math.min(start + stripeRows, height)])
    return ranges
  })
  const outputBytes = Math.max(header.getUint32(16, true) * header.getUint32(20, true) * 4,
    run.layout.pixelCount * 4, probeCount * 4)
  const end = run.outputAddress + outputBytes
  if (end > memory.buffer.byteLength) memory.grow(Math.ceil((end - memory.buffer.byteLength) / 65536))
  const control = new Int32Array(new SharedArrayBuffer((count + 1) * 64))
  // Cumulative work, last start and last end are published before completion.
  const workTimes = Array.from({ length: count }, (_, index) => new Float64Array(control.buffer, (completion(index) + 2) * 4, 3))
  const cancel = cancellation ?? new Int32Array(new SharedArrayBuffer(4))
  const workers = []
  const startupTimers = new Set()
  const args = [run.packageAddress, run.packageBytes, run.stateAddress, run.layout.stateBytes]
  let failure, closed = false, advancing = false, generation = 0
  let coordinatorMilliseconds = 0
  let workBaseline = workTimes.map(() => 0)
  let barriers = 0, barrierMilliseconds = 0, resumeMilliseconds = 0
  const scheduling = Array.from({ length: count }, (_, index) => ({
    rowStart: bands[index][0][0], rowEnd: bands[index].at(-1)[1],
    rowCount: bands[index].reduce((rows, [start, end]) => rows + end - start, 0),
    stripeRows, stripeCount: bands[index].length,
    calls: 0, maxCallMilliseconds: 0, dispatchMilliseconds: 0, maxDispatchMilliseconds: 0,
    earlyFinishMilliseconds: 0, waitForWorkMilliseconds: 0, lastFinishes: 0,
  }))
  const previousEnds = Array(count).fill(undefined)
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
      const worker = createWorker(index)
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
        timeOrigin: performance.timeOrigin,
        stackTop: heap + (index + 1) * stackBytes, args,
        workerCount: count, rowsPerStripe: stripeRows })
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
  function recordBarrier(published, resumed) {
    let lastEnd = -Infinity, lastIndex = 0
    for (let index = 0; index < count; index++) {
      if (workTimes[index][2] > lastEnd) { lastEnd = workTimes[index][2]; lastIndex = index }
    }
    barriers++
    barrierMilliseconds += resumed - published
    resumeMilliseconds += Math.max(0, resumed - lastEnd)
    for (let index = 0; index < count; index++) {
      const started = workTimes[index][1], ended = workTimes[index][2]
      const stats = scheduling[index]
      const dispatch = Math.max(0, started - published)
      stats.calls++
      stats.maxCallMilliseconds = Math.max(stats.maxCallMilliseconds, ended - started)
      stats.dispatchMilliseconds += dispatch
      stats.maxDispatchMilliseconds = Math.max(stats.maxDispatchMilliseconds, dispatch)
      stats.earlyFinishMilliseconds += Math.max(0, lastEnd - ended)
      if (previousEnds[index] != null) stats.waitForWorkMilliseconds += Math.max(0, published - previousEnds[index])
      if (index === lastIndex) stats.lastFinishes++
      previousEnds[index] = ended
    }
  }
  return Object.freeze({ ...run, workerCount: count, rowsPerStripe: stripeRows, close,
    timings() {
      if (advancing) throw new Error('Cannot read timings during an advance.')
      return { coordinatorMilliseconds: coordinatorMilliseconds + run.timings().coordinatorMilliseconds,
        workerMilliseconds: workTimes.map((value, index) => value[0] - workBaseline[index]),
        scheduling: { barriers, barrierMilliseconds, resumeMilliseconds, workers: scheduling.map(stats => ({ ...stats })) } }
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
            const published = performance.now()
            Atomics.store(control, 0, generation)
            Atomics.notify(control, 0)
            await barrier()
            recordBarrier(published, performance.now())
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
      barriers = 0
      barrierMilliseconds = 0
      resumeMilliseconds = 0
      previousEnds.fill(undefined)
      for (const stats of scheduling) Object.assign(stats, { calls: 0, maxCallMilliseconds: 0,
        dispatchMilliseconds: 0, maxDispatchMilliseconds: 0, earlyFinishMilliseconds: 0,
        waitForWorkMilliseconds: 0, lastFinishes: 0 })
    },
    observe() { ensureActive(); if (advancing) throw new Error('Cannot observe during an advance.'); return run.observe() },
    capture() { ensureActive(); if (advancing) throw new Error('Cannot capture during an advance.'); return run.capture() },
  })
}

/** Run in a compute worker after receiving its immutable setup. No messages or
 * row claims occur per substep. Each generation processes all owned stripes before
 * one completion notification. Timed idle waits allow cancellation even if the
 * coordinator was terminated before it could publish a shutdown generation.
 */
export async function runComputeWorker(setup, postMessage) {
  const { module, memory, control, cancellation, index, stackTop, args, workerCount, rowsPerStripe, timeOrigin } = setup
  const word = completion(index)
  const workTime = new Float64Array(control.buffer, (word + 2) * 4, 3)
  // Worker and coordinator performance.now() values have different origins.
  // Translate only the timestamps; keep local differences for cumulative work.
  const clockOffset = performance.timeOrigin - timeOrigin
  let workMilliseconds = 0
  try {
    const { exports } = await WebAssembly.instantiate(module, { env: { memory } })
    exports.__stack_pointer.value = stackTop
    const stripeArgs = [...args, index, workerCount, rowsPerStripe]
    let seen = 0
    postMessage({ type: 'ready' })
    while (!Atomics.load(control, 1) && !Atomics.load(cancellation, 0)) {
      const generation = Atomics.load(control, 0)
      if (generation === seen) { Atomics.wait(control, 0, seen, 100); continue }
      const started = performance.now()
      const status = exports.kernel_step_stripes(...stripeArgs)
      const ended = performance.now()
      workMilliseconds += ended - started
      workTime[0] = workMilliseconds
      workTime[1] = started + clockOffset
      workTime[2] = ended + clockOffset
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
