import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { loadWaveDude } from '../public/wasm/main.mjs'
import { prepareRustSimulation } from '../src/Wasm/PreparedSimulation.ts'
import { createRustSimulation } from '../src/Wasm/RustSimulation.ts'
import { decodeProbeFrame, ProbeSeries } from '../src/Wasm/ProbeFrames.ts'
import { readFrame, writeFrame } from '../src/Wasm/FrameStore.ts'
import { halfValue } from '../src/Rendering/FrameColours.ts'

const module = await WebAssembly.compile(await readFile(new URL('../src/assets/rust-kernel.wasm', import.meta.url)))
const example = JSON.parse(await readFile(new URL('../src/assets/examples/Example01_Source_Mirror_Probe.json', import.meta.url), 'utf8'))
const project = structuredClone(example)
Object.assign(project.scene, { width: 19, height: 13, dampingEdge: -3 })
Object.assign(project.simulation, { steps: 25, visualizeEvery: 3 })
project.parts = []
project.sources = [{ ...project.sources[0], x: 4, y: 5 }]
const point = { ...project.probes[0], active: true, ax: 4, ay: 5, bx: 14, by: 8, kind: 'Point', layer: 'Field', measurement: 'Amplitude', startAfter: 5 }
// Interleave line and point probes, including a disabled entry and a one-pixel line.
project.probes = [
  { ...point, active: false },
  { ...point, kind: 'Line', layer: 'Intensity', measurement: 'IntegratedValue' },
  point,
  { ...point, kind: 'Line', bx: 4, by: 5 },
  { ...point, layer: 'Intensity', measurement: 'IntegratedValue' },
  { ...point, kind: 'Line', layer: 'Intensity', measurement: 'Value' },
  { ...point, layer: 'Intensity', measurement: 'Value' },
]

function near(actual, expected, absolute = 2e-7, relative = 3e-4) {
  assert.equal(actual.length, expected.length)
  actual.forEach((value, i) => assert.ok(Number.isFinite(value)
    && Math.abs(value - expected[i]) <= absolute + relative * Math.abs(expected[i]), `${value} versus ${expected[i]} at ${i}`))
}

// Use the production storage functions with closed, in-memory File handles.
function frameDirectory() {
  const files = new Map()
  return { async getFileHandle(name) { return {
    async createWritable() {
      let bytes
      return {
        async write(blob) { bytes = await blob.arrayBuffer() },
        async close() { files.set(name, new File([bytes], name)) },
        async abort() {},
      }
    },
    async getFile() { return files.get(name) },
  } } }
}

// The .NET host may unref startup work before it has completed.
const keepAlive = setInterval(() => {}, 1000)
try {
  const runtime = await loadWaveDude()
  // The application boundary exposes preparation only, never the C# solving exports.
  const preparation = { Prepare: runtime.Prepare.bind(runtime) }
  const run = async (input, maxFrames) => {
    const json = JSON.stringify(input)
    const prepared = prepareRustSimulation(preparation, json, maxFrames)
    const packet = prepared.packet.slice()
    const { exports } = await WebAssembly.instantiate(module)
    const simulation = createRustSimulation(exports, prepared)
    const reference = runtime.StartSimulation(json, maxFrames)
    assert.deepEqual(Array.from(reference), [prepared.width, prepared.height, prepared.totalSteps, prepared.visualizeEvery, prepared.totalFrames])
    const directory = frameDirectory()
    const active = input.probes.filter(probe => probe.active)
    const series = active.map(() => new ProbeSeries())
    const frames = []
    try {
      for (let index = 0; index < prepared.totalFrames; index++) {
        const frame = simulation.nextFrame()
        const expected = runtime.NextFrame()
        const expectedProbes = decodeProbeFrame(runtime.GetProbeFrame())
        near(Array.from(new Uint16Array(frame.bytes.buffer), halfValue), Array.from(new Uint16Array(expected.buffer, expected.byteOffset, expected.byteLength / 2), halfValue), 1e-6, .002)
        const probes = decodeProbeFrame(frame.probeBytes)
        assert.equal(probes.stepsSampled, index * prepared.visualizeEvery + 1)
        assert.equal(probes.probes.length, active.length)
        probes.probes.forEach((probe, i) => {
          const expected = expectedProbes.probes[i]
          assert.equal(probe.profileLength, expected.profileLength)
          assert.equal(probe.profile === null, expected.profile === null)
          near(probe.samples, expected.samples)
          if (probe.profile) near(probe.profile, expected.profile)
          series[i].append(probe.samples.slice())
        })
        await writeFrame(directory, index, frame.bytes, frame.probeBytes)
        frames.push({ ...frame, probes })
      }
      assert.throws(() => simulation.nextFrame(), /no more preview frames/)
      assert.deepEqual(prepared.packet, packet, 'Solving preserves immutable preparation inputs')
      for (const index of [frames.length - 1, 0, Math.floor(frames.length / 2)]) {
        const selected = await readFrame(directory, 'rust-run', index, prepared.width, prepared.height, active, series, prepared.visualizeEvery)
        assert.deepEqual(selected.field.samples, new Uint16Array(frames[index].bytes.buffer, 0, prepared.width * prepared.height))
        selected.probes.probes.forEach((probe, i) => {
          assert.deepEqual(probe.profile, frames[index].probes.probes[i].profile)
          const history = frames.slice(0, index + 1).flatMap(frame => Array.from(frame.probes.probes[i].samples))
          assert.deepEqual(probe.values, Float32Array.from(history), 'Seeking exposes exactly the selected history prefix')
        })
      }
    } finally {
      runtime.EndSimulation()
    }
    return { prepared, frames }
  }

  const sparse = await run(project, 100)
  const repeat = await run(project, 100)
  assert.deepEqual(repeat.frames, sparse.frames, 'Independent Rust runs are deterministic')
  const dense = await run({ ...project, simulation: { ...project.simulation, visualizeEvery: 1 } }, 100)
  sparse.frames.forEach((frame, index) => {
    const reference = dense.frames[index * sparse.prepared.visualizeEvery]
    assert.deepEqual(frame.bytes, reference.bytes, 'Capture spacing does not change the solver')
    frame.probes.probes.forEach((probe, i) => {
      assert.deepEqual(probe.profile, reference.probes.probes[i].profile)
      if (probe.samples.length) assert.deepEqual(probe.samples,
        Float32Array.from(dense.frames.slice(index ? (index - 1) * sparse.prepared.visualizeEvery + 1 : 0, index * sparse.prepared.visualizeEvery + 1)
          .map(frame => frame.probes.probes[i].samples[0])))
    })
  })
  const limited = await run(project, 2)
  assert.equal(limited.prepared.totalFrames, 2)
  assert.ok(limited.prepared.visualizeEvery > project.simulation.visualizeEvery, 'Storage budgets widen captures')
  await run({ ...project, probes: [], sources: [], simulation: { ...project.simulation, steps: 1 } }, 100)
  console.log('Rust frontend solver, probe packets, deterministic runs, storage budgets, frame storage and seeking checks passed.')
} finally {
  clearInterval(keepAlive)
}
