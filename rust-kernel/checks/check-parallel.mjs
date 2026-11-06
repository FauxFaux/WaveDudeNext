import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { Worker, isMainThread, parentPort, workerData } from 'node:worker_threads'
import { prepareKernel } from '../interface.mjs'
import { createParallelKernel, runComputeWorker } from '../parallel.mjs'

if (!isMainThread) {
  parentPort.once('message', async setup => {
    if (workerData === 'startup') throw new Error('Injected startup failure')
    if (workerData === 'crash' || workerData === 'silent') {
      parentPort.postMessage({ type: 'ready' })
      while (!Atomics.load(setup.control, 0) && !Atomics.load(setup.control, 1))
        Atomics.wait(setup.control, 0, 0, 100)
      if (workerData === 'crash') throw new Error('Injected compute worker failure')
      while (!Atomics.load(setup.control, 1)) Atomics.wait(setup.control, 0, Atomics.load(setup.control, 0), 100)
    } else {
      await runComputeWorker(setup, message => parentPort.postMessage(message))
    }
    parentPort.close()
  })
} else {
  const keepAlive = setInterval(() => {}, 1000)
  try {
    const { loadWaveDude } = await import('../../web/public/wasm/main.mjs')
    const { decodePreparedSimulation } = await import('../../web/src/Wasm/PreparedSimulation.ts')
    const runtime = await loadWaveDude()
    const ordinary = await WebAssembly.compile(await readFile(new URL('../../web/src/assets/rust-kernel.wasm', import.meta.url)))
    const shared = await WebAssembly.compile(await readFile(new URL('../../web/src/assets/rust-kernel-shared.wasm', import.meta.url)))
    assert.deepEqual(WebAssembly.Module.imports(shared), [{ module: 'env', name: 'memory', kind: 'memory' }])
    const example = JSON.parse(await readFile(new URL('../../web/src/assets/examples/Example01_Source_Mirror_Probe.json', import.meta.url), 'utf8'))
    const project = structuredClone(example)
    Object.assign(project.scene, { width: 37, height: 23, dampingEdge: -3 })
    Object.assign(project.simulation, { steps: 30, visualizeEvery: 4 })
    project.parts = []
    project.sources = [{ ...project.sources[0], x: 2, y: 2 }, { ...project.sources[0], x: 2, y: 2, phase: 45 }]
    const point = { ...project.probes[0], active: true, ax: 2, ay: 2, bx: 30, by: 18, startAfter: 5 }
    project.probes = [
      { ...point, kind: 'Point', layer: 'Field', measurement: 'Amplitude' },
      { ...point, kind: 'Point', layer: 'Intensity', measurement: 'IntegratedValue' },
      { ...point, kind: 'Line', layer: 'Intensity', measurement: 'IntegratedValue' },
    ]
    function createWorker(mode, live = []) {
      const node = new Worker(new URL(import.meta.url), { workerData: mode })
      const wrapper = { postMessage: value => node.postMessage(value), terminate: () => node.terminate(), node }
      node.on('message', data => wrapper.onmessage?.({ data }))
      node.on('error', error => wrapper.onerror?.(error))
      live.push(node)
      return wrapper
    }
    const prepare = input => decodePreparedSimulation(runtime.Prepare(JSON.stringify(input), 100))
    let cases = 0
    for (const fourth of [false, true]) {
      for (const [rim, edge] of [[0, 'Reflective'], [0, 'Open'], [-3, 'Open']]) {
        const input = structuredClone(project)
        input.scene.dampingEdge = rim
        input.scene.edges = edge
        input.simulation.fourthOrderLaplacian = fourth
        const prepared = prepare(input)
        assert.equal(prepared.fourthOrder, fourth)
        if (fourth) assert.ok(prepared.subSteps > 1)
        for (const count of [1, 2, 4]) {
          const serial = prepareKernel((await WebAssembly.instantiate(ordinary)).exports, prepared)
          const packet = prepared.packet.slice()
          const parallel = await createParallelKernel(shared, prepared, { workerCount: count, createWorker: () => createWorker() })
          try {
            const compare = () => {
              for (const name of ['control', 'field0', 'field1', 'intensityStage1', 'intensityStage2', 'intensityOutput', 'probeTotals'])
                assert.deepEqual(parallel.views()[name], serial.views()[name], `${name}: ${count} workers, fourth=${fourth}, rim=${rim}, edge=${edge}`)
              assert.deepEqual(parallel.capture(), serial.capture())
              assert.deepEqual(parallel.observe(), serial.observe())
            }
            compare() // Additional instances must not reinitialise the package/state.
            let previousTimings = parallel.timings()
            for (const steps of [1, 4, 4, 4]) {
              assert.deepEqual(await parallel.advance(steps), serial.advance(steps))
              compare()
              const timings = parallel.timings()
              assert.ok(timings.coordinatorMilliseconds >= previousTimings.coordinatorMilliseconds)
              assert.equal(timings.workerMilliseconds.length, count)
              timings.workerMilliseconds.forEach((time, index) => assert.ok(Number.isFinite(time) && time > previousTimings.workerMilliseconds[index]))
              previousTimings = timings
            }
            parallel.reset()
            serial.reset()
            assert.deepEqual(parallel.timings(), { coordinatorMilliseconds: 0, workerMilliseconds: Array(count).fill(0) })
            assert.deepEqual(await parallel.advance(4), serial.advance(4))
            compare()
            assert.deepEqual(prepared.packet, packet)
            await assert.rejects(parallel.advance(0xffffffff), /step count/)
            const pending = parallel.advance(1)
            const concurrent = parallel.advance(1)
            assert.throws(() => parallel.reset(), /during an advance/)
            await assert.rejects(concurrent, /Concurrent/)
            await pending
          } finally { parallel.close() }
          cases++
        }
      }
    }
    // Tiny grids, more requested workers than rows, no sources/probes, multi-substeps.
    const empty = structuredClone(project)
    Object.assign(empty.scene, { width: 8, height: 8, dampingEdge: 0 })
    empty.sources = []
    empty.probes = []
    const tiny = prepare(empty)
    const many = await createParallelKernel(shared, tiny, { workerCount: 10, createWorker: () => createWorker() })
    try {
      assert.equal(many.workerCount, tiny.gridHeight)
      await many.advance(4)
      assert.ok(many.capture().every(value => value === 0))
    } finally { many.close() }

    for (const mode of ['crash', 'silent']) {
      const live = []
      const pool = await createParallelKernel(shared, prepare(project), { workerCount: 2, timeoutMs: 1000,
        createWorker: () => createWorker(mode, live) })
      const exits = live.map(node => new Promise(resolve => node.once('exit', resolve)))
      await assert.rejects(pool.advance(1), mode === 'crash' ? /Injected|worker failed/ : /timed out/)
      await Promise.all(exits)
      pool.close()
    }
    const startup = []
    await assert.rejects(createParallelKernel(shared, prepare(project), { workerCount: 2,
      createWorker: () => createWorker('startup', startup) }), /Injected startup failure/)
    assert.ok(startup.length > 0)
    // Cancel while a generation is in flight; it must reject instead of publishing a frame.
    const activeCancellation = new Int32Array(new SharedArrayBuffer(4))
    const active = await createParallelKernel(shared, prepare(project), { workerCount: 2,
      cancellation: activeCancellation, createWorker: () => createWorker() })
    const pending = active.advance(4)
    Atomics.store(activeCancellation, 0, 1)
    await assert.rejects(pending, /cancelled/)
    active.close()
    // Simulate termination of the coordinator: cancellation alone must release idle children.
    const cancellation = new Int32Array(new SharedArrayBuffer(4))
    const live = []
    const pool = await createParallelKernel(shared, prepare(project), { workerCount: 2, cancellation,
      createWorker: () => createWorker(undefined, live) })
    const exits = live.map(node => new Promise(resolve => node.once('exit', resolve)))
    Atomics.store(cancellation, 0, 1)
    await Promise.all(exits)
    await assert.rejects(pool.advance(1), /cancelled/)
    pool.close()
    console.log(`Shared Rust kernel passed ${cases} single/band parity cases, reset, concurrent-call guards, cancellation, worker failure and timeout cleanup.`)
  } finally { clearInterval(keepAlive) }
}
