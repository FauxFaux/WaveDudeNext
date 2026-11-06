import assert from 'node:assert/strict'
import { decodePreparedSimulation, prepareRustSimulation } from '../src/Wasm/PreparedSimulation.ts'
import type { WaveDudeRuntime } from '../src/Wasm/Runtime.ts'
import type { Project } from '../src/Model/Project.ts'

/** Exercise the production decoder against C# output, including failures before kernel hand-off. */
export function checkPreparedSimulation(runtime: WaveDudeRuntime, example: Project) {
  const project = structuredClone(example)
  Object.assign(project.scene, { width: 13, height: 9, dampingEdge: -2, refractiveIndex: 1.7 })
  Object.assign(project.simulation, { steps: 9, visualizeEvery: 2 })
  project.parts = []
  const source = { ...project.sources[0], x: 2, y: 3, phase: 60, amplitude: 75 }
  project.sources = [source, { ...source, amplitude: 25, detachAfter: 4 },
    { ...source, enabled: false }, { ...source, x: -1 }]
  const point = { ...project.probes[0], kind: 'Point' as const, layer: 'Field' as const, ax: 2, ay: 3, bx: 5, by: 3,
    measurement: 'Amplitude' as const, startAfter: 0 }
  project.probes = [{ ...point, active: false }, point,
    { ...point, layer: 'Intensity', measurement: 'IntegratedValue', startAfter: 2 },
    { ...point, kind: 'Line', layer: 'Intensity', measurement: 'IntegratedValue', startAfter: 2 }]

  for (const fourthOrderLaplacian of [false, true]) for (const edges of ['Open', 'Reflective'] as const) {
    project.simulation.fourthOrderLaplacian = fourthOrderLaplacian
    project.scene.edges = edges
    const json = JSON.stringify(project)
    const prepared = prepareRustSimulation(runtime, json, 3)
    assert.deepEqual([prepared.width, prepared.height, prepared.gridWidth, prepared.gridHeight, prepared.rim,
      prepared.stride, prepared.cellCount], [13, 9, 17, 13, 2, 19, 285])
    assert.equal(prepared.openEdges, edges === 'Open')
    assert.equal(prepared.fourthOrder, fourthOrderLaplacian)
    assert.equal(prepared.trackIntensity, true)
    assert.equal(prepared.subSteps, fourthOrderLaplacian ? 2 : 1)
    assert.equal(prepared.c2Multiplier, Math.fround(1 / prepared.subSteps ** 2))
    assert.equal(prepared.intensityAlpha, Math.fround(Math.exp(-1 / 13)))
    assert.equal(prepared.detachFadeSteps, 30)
    assert.equal(prepared.emitters.length, 2, 'Disabled and off-canvas sources are filtered by C#')
    assert.deepEqual(prepared.emitters.map(emitter => emitter.amplitude), [.75, .25], 'Overlapping emitter order survives decoding')
    assert.equal(prepared.emitters[0].index, 6 * 19 + 5)
    assert.equal(prepared.emitters[0].mode, 1)
    assert.equal(prepared.emitters[0].pulseShape, 1)
    assert.equal(prepared.emitters[0].omega, Math.fround(100 / 255))
    assert.equal(prepared.emitters[0].phase, Math.PI / 3)
    assert.equal(prepared.emitters[1].detachAfterStep, 4)
    assert.deepEqual(prepared.probes.map(probe => probe.projectIndex), [1, 2, 3])
    assert.deepEqual(prepared.probes.map(probe => [probe.isProfile, probe.useIntensity, probe.cumulative, probe.integrate, probe.useStartAfter]),
      [[false, false, false, false, false], [false, true, true, false, true], [true, true, false, true, true]])
    assert.deepEqual(Array.from(prepared.probes[2].pixels), [119, 120, 121, 122])
    assert.equal(prepared.probes[2].length, 3)
    assert.equal(prepared.probes[2].startAfter, 2)
    assert.equal(prepared.c2.buffer, prepared.packet.buffer, 'Maps are views of the retained packet')
    assert.equal(prepared.damping.byteOffset, prepared.packet.byteOffset + prepared.offsets.damping)
    assert.equal(prepared.c2[119], Math.fround(Math.fround(1 / 1.7) ** 2))
    assert.equal(prepared.damping[20], fourthOrderLaplacian ? Math.fround(Math.sqrt(.5)) : .5)
    assert.deepEqual(Array.from(runtime.StartSimulation(json, 3)),
      [prepared.width, prepared.height, prepared.totalSteps, prepared.visualizeEvery, prepared.totalFrames])
    runtime.NextFrame()
    const before = prepared.packet.slice()
    runtime.EndSimulation()
    runtime.RenderScene(json)
    assert.deepEqual(prepared.packet, before, 'The JS model outlives C# engine and scene calls')
  }

  const json = JSON.stringify(project)
  const packet = runtime.Prepare(json, 3)
  let calls = 0
  const prepared = prepareRustSimulation({ Prepare(input, budget) {
    calls++
    assert.equal(input, json)
    assert.equal(budget, 3)
    return packet
  } }, json, 3)
  assert.equal(calls, 1, 'Run submits one preparation request with its frame budget')
  for (const base of [1, 64]) {
    const memory = new WebAssembly.Memory({ initial: Math.ceil((base + packet.length) / 65536) })
    const relocated = new Uint8Array(memory.buffer, base, packet.length)
    relocated.set(packet)
    const decoded = decodePreparedSimulation(relocated)
    assert.deepEqual(decoded.c2, prepared.c2, 'Unaligned and relocated packet views decode correctly')
    assert.deepEqual(decoded.emitters, prepared.emitters, 'Relocation preserves padded cell indices')
    assert.deepEqual(decoded.probes, prepared.probes)
  }

  const reject = (change: (view: DataView, bytes: Uint8Array<ArrayBuffer>) => void) => {
    const bytes = packet.slice()
    change(new DataView(bytes.buffer), bytes)
    assert.throws(() => decodePreparedSimulation(bytes), /Invalid simulation preparation/)
  }
  assert.throws(() => decodePreparedSimulation(packet.subarray(0, 159)), /incomplete header/)
  for (const [offset, value] of [[0, 0], [4, 2], [8, 160], [12, 144], [16, 0], [36, 1], [40, 0],
    [44, 15], [44, 0], [48, 0], [56, 0], [60, 0], [64, 0], [68, 0xffffffff], [76, 0xffffffff],
    [80, 161], [84, 160], [96, packet.length], [100, 1], [144, 32], [148, 64], [156, 1]])
    reject(view => view.setUint32(offset, value, true))
  reject(view => view.setFloat32(104, NaN, true))
  reject(view => view.setFloat32(108, 2, true))
  reject(view => view.setFloat32(prepared.offsets.c2, Infinity, true))
  reject(view => view.setFloat32(prepared.offsets.damping, -1, true))
  for (const [offset, value] of [[0, prepared.cellCount], [4, 2], [8, 2], [12, 0], [36, 1]])
    reject(view => view.setUint32(prepared.offsets.emitters + offset, value, true))
  reject(view => view.setFloat64(prepared.offsets.emitters + 40, Infinity, true))
  for (const [offset, value] of [[0, 1], [4, 0], [8, 32], [28, 1]])
    reject(view => view.setUint32(prepared.offsets.probes + offset, value, true))
  reject(view => view.setUint32(prepared.offsets.pixels, 0, true))
  reject(view => view.setFloat64(prepared.offsets.probes + 16, NaN, true))
  reject((_view, bytes) => { bytes[prepared.offsets.emitters - 1] = 1 })

  project.scene.objectSources = false
  project.scene.dampingEdge = 0
  project.probes = []
  const empty = prepareRustSimulation(runtime, JSON.stringify(project), 100)
  assert.equal(empty.rim, 0)
  assert.equal(empty.emitters.length, 0)
  assert.equal(empty.probes.length, 0, 'Empty aligned sections are valid')
  project.scene.mode = 'Image'
  assert.throws(() => prepareRustSimulation(runtime, JSON.stringify(project), 100), /Image scenes are not supported/)
  assert.throws(() => prepareRustSimulation(runtime, json, 0))
}
