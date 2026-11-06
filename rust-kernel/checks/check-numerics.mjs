// SPDX-License-Identifier: GPL-3.0-or-later

import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { prepareKernel } from '../interface.mjs'
import { decodePreparedSimulation } from '../../web/src/Wasm/PreparedSimulation.ts'
import { loadWaveDude } from '../../web/public/wasm/main.mjs'

// This runs the real published C# solver and Rust SIMD kernel against the same
// preparation packet. No second solver or generated fixtures live in the checkout.
const keepAlive = setInterval(() => {}, 1000)
const module = await WebAssembly.compile(await readFile(new URL('../../web/src/assets/rust-kernel.wasm', import.meta.url)))
const { exports: kernel } = await WebAssembly.instantiate(module)
const example = JSON.parse(await readFile(new URL('../../web/src/assets/examples/Example01_Source_Mirror_Probe.json', import.meta.url), 'utf8'))
let checkedFrames = 0, checkedValues = 0, maximumProbeError = 0
function near(actual, expected, absolute, relative, message) {
  assert.ok(Number.isFinite(actual) && Number.isFinite(expected), `${message}: nonfinite value`)
  const error = Math.abs(actual - expected)
  assert.ok(error <= absolute + relative * Math.abs(expected), `${message}: ${actual} versus ${expected}; error ${error}`)
  checkedValues++
  return error
}
function half(bits) {
  const sign = bits & 0x8000 ? -1 : 1
  const exponent = (bits >>> 10) & 31
  const fraction = bits & 1023
  return sign * (exponent ? (1 + fraction / 1024) * 2 ** (exponent - 15) : fraction * 2 ** -24)
}
function compareFrames(actual, expected, label) {
  assert.equal(actual.length, expected.length)
  const a = new DataView(actual.buffer, actual.byteOffset, actual.byteLength)
  const b = new DataView(expected.buffer, expected.byteOffset, expected.byteLength)
  for (let at = 0; at < actual.length; at += 2) {
    assert.notEqual(a.getUint16(at, true) & 0x7c00, 0x7c00, `${label}: f16 nonfinite`)
    assert.notEqual(b.getUint16(at, true) & 0x7c00, 0x7c00, `${label}: C# f16 nonfinite`)
    near(half(a.getUint16(at, true)), half(b.getUint16(at, true)), 1e-6, .002, `${label} half ${at / 2}`)
  }
  checkedFrames++
}
function compareProbes(prepared, samples, profiles, packet, completed, steps, label) {
  const view = new DataView(packet.buffer, packet.byteOffset, packet.byteLength)
  assert.equal(view.getInt32(0, true), prepared.probes.length)
  assert.equal(view.getInt32(4, true), completed)
  let at = 8, pixel = 0
  for (const [i, probe] of prepared.probes.entries()) {
    const count = view.getInt32(at, true), profileCount = view.getInt32(at + 4, true)
    assert.equal(view.getFloat64(at + 8, true), probe.length)
    assert.equal(count, probe.isProfile ? 0 : steps)
    assert.equal(profileCount, probe.isProfile ? probe.pixels.length : -1)
    at += 16
    for (let n = 0; n < count; n++, at += 4) {
      const error = near(samples[n * prepared.probes.length + i], view.getFloat32(at, true), 2e-7, 3e-4, `${label} point ${i} step ${completed - steps + n + 1}`)
      maximumProbeError = Math.max(maximumProbeError, error)
    }
    for (let n = 0; n < profileCount; n++, at += 4) {
      const error = near(profiles[pixel + n], view.getFloat32(at, true), 2e-7, 3e-4, `${label} profile ${i} pixel ${n}`)
      maximumProbeError = Math.max(maximumProbeError, error)
    }
    pixel += probe.pixels.length
  }
  assert.equal(at, packet.length)
}
function smallProject() {
  const project = structuredClone(example)
  Object.assign(project.scene, { width: 13, height: 9, dampingEdge: 0, refractiveIndex: 1.0 })
  Object.assign(project.simulation, { steps: 181, visualizeEvery: 17, intensityScale: 2.5 })
  project.parts = []
  project.sources = [{ ...project.sources[0], x: 2, y: 3, mode: 'Continuous', phase: 60, amplitude: 75, frequency: 200,
    pulseWidth: 1, offWidth: 1, detachAfter: -1 }]
  const point = { ...project.probes[0], ax: 2, ay: 3, bx: 11, by: 7, kind: 'Point', layer: 'Field', measurement: 'Amplitude', startAfter: 20 }
  project.probes = [
    { ...point, active: false }, point,
    { ...point, layer: 'Intensity', measurement: 'Value' },
    { ...point, layer: 'Intensity', measurement: 'IntegratedValue' },
    { ...point, kind: 'Line' },
    { ...point, kind: 'Line', layer: 'Intensity', measurement: 'Value' },
    { ...point, kind: 'Line', layer: 'Intensity', measurement: 'IntegratedValue' },
    { ...point, kind: 'Line', ax: 2, ay: 3, bx: 2, by: 3, layer: 'Intensity', measurement: 'IntegratedValue', startAfter: 1 },
  ]
  return project
}

try {
  const runtime = await loadWaveDude()
  const cases = []
  for (const fourth of [false, true]) for (const edges of ['Open', 'Reflective']) for (const damping of [-3, 0, 2]) {
    const project = smallProject()
    project.simulation.fourthOrderLaplacian = fourth
    project.scene.edges = edges
    project.scene.dampingEdge = damping
    cases.push([`stencil=${fourth ? 4 : 9} ${edges} damping=${damping}`, project])
  }
  for (const fourth of [false, true]) for (const shape of ['Gaussian', 'SmoothedSquare']) for (const random of [0, 2]) {
    const project = smallProject()
    project.simulation.fourthOrderLaplacian = fourth
    Object.assign(project.simulation, { steps: 261, visualizeEvery: 31 })
    project.scene.dampingEdge = -2
    Object.assign(project.sources[0], { mode: 'Pulsed', pulseShape: shape, offWidth: 2 })
    if (random) project.sources[0].array = { sizeX: 3, sizeY: 1, spacingX: 1, spacingY: 1, angle: 0,
      randomFrequency: 5, randomPhase: 20, randomOffWidth: random, randomPosition: 0 }
    cases.push([`pulse=${shape} random=${random} fourth=${fourth}`, project])
  }
  for (const fourth of [false, true]) {
    for (const size of [[8, 8], [9, 8], [10, 9], [11, 8]]) {
      const project = smallProject()
      Object.assign(project.scene, { width: size[0], height: size[1] })
      project.simulation.fourthOrderLaplacian = fourth
      cases.push([`tiny ${size} fourth=${fourth}`, project])
    }
    const project = smallProject()
    project.simulation.fourthOrderLaplacian = fourth
    project.scene.refractiveIndex = 2.7 // Fourth-order without stability substeps.
    project.sources.push({ ...project.sources[0], name: 'Overlapping', phase: 135, frequency: 80, detachAfter: 9 })
    project.sources[0].detachAfter = 0
    cases.push([`overlap/detach fourth=${fourth}`, project])
    const painted = smallProject()
    painted.simulation.fourthOrderLaplacian = fourth
    painted.parts = [{ ...example.parts[0], x: 7, y: 3, width: 3, length: 2, angle: 20, absorption: 100 }]
    cases.push([`painted mirror fourth=${fourth}`, painted])
  }
  const zeroFrequency = smallProject()
  zeroFrequency.simulation.sourceFrequencyScale = 0
  cases.push(['zero frequency', zeroFrequency])
  const empty = smallProject()
  empty.sources = []
  empty.probes = []
  cases.push(['no sources or probes', empty])

  for (const [label, project] of cases) {
    const json = JSON.stringify(project)
    const prepared = decodePreparedSimulation(runtime.Prepare(json, 24))
    const run = prepareKernel(kernel, prepared)
    const inputCopy = prepared.packet.slice()
    const metadata = runtime.StartSimulation(json, 24)
    assert.deepEqual(Array.from(metadata), [prepared.width, prepared.height, prepared.totalSteps, prepared.visualizeEvery, prepared.totalFrames])
    let completed = 0
    for (let k = 0; k < prepared.totalFrames; k++) {
      const target = k * prepared.visualizeEvery + 1
      const steps = target - completed
      const samples = run.advance(steps)
      completed = target
      compareFrames(run.capture(), runtime.NextFrame(), `${label} frame ${k}`)
      compareProbes(prepared, samples, run.observe(), runtime.GetProbeFrame(), completed, steps, label)
      const views = run.views()
      assert.equal(views.control[0], completed)
      assert.equal(views.control[1], (completed * prepared.subSteps) % 2)
      for (const data of [views.field0, views.field1, views.intensityStage1, views.intensityStage2, views.intensityOutput])
        assert.ok(data.every(Number.isFinite), `${label}: stable full-precision state`)
    }
    assert.equal(completed, prepared.totalSteps)
    assert.throws(() => run.advance(1), /invalid state/)
    assert.deepEqual(new Uint8Array(kernel.memory.buffer, run.packageAddress, prepared.packet.length), inputCopy)
    runtime.EndSimulation()
    // Reset must reproduce a fresh run, including pulse/RNG state and both filters.
    run.reset()
    const first = run.advance(1), frame = run.capture()
    run.reset()
    assert.deepEqual(run.advance(1), first)
    assert.deepEqual(run.capture(), frame)
    console.log(`Passed ${label} (${prepared.width}x${prepared.height}, ${prepared.subSteps} substeps, ${completed} steps)`)
  }

  // A real example adds a larger heterogeneous map and the unmodified browser input.
  const json = JSON.stringify(example)
  const prepared = decodePreparedSimulation(runtime.Prepare(json, 2500))
  const run = prepareKernel(kernel, prepared)
  runtime.StartSimulation(json, 2500)
  for (let k = 0; k <= 4; k++) {
    const steps = k === 0 ? 1 : prepared.visualizeEvery
    const samples = run.advance(steps)
    compareFrames(run.capture(), runtime.NextFrame(), `Example 1 capture ${k}`)
    compareProbes(prepared, samples, run.observe(), runtime.GetProbeFrame(), k * prepared.visualizeEvery + 1, steps, 'Example 1')
  }
  runtime.EndSimulation()
  console.log(`Rust/C# numerical equivalence passed: ${cases.length} cases, ${checkedFrames} frames, ${checkedValues} values; max absolute f32 probe error ${maximumProbeError}.`)
} catch (error) {
  console.error(String(error))
  process.exitCode = 1
} finally {
  clearInterval(keepAlive)
}
