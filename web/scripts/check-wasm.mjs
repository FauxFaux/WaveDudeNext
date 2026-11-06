import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { createHash } from 'node:crypto'
import { loadWaveDude } from '../public/wasm/main.mjs'
import { decodeScene, sceneKey } from '../src/Wasm/Scene.ts'

// The .NET Node host may unref its startup work; keep Node alive until the check finishes.
const keepAlive = setInterval(() => {}, 1000)
try {
  const runtime = await loadWaveDude()
  assert.equal(await loadWaveDude(), runtime, 'The host reuses one runtime per JS context')
  const peak = runtime.SmokeTest()
  assert.ok(Number.isFinite(peak) && peak > 0, 'The shared solver produces a finite nonzero field')
  assert.equal(runtime.SmokeTest(), peak, 'The scene is deterministic across independent runs')

  const project = JSON.parse(await readFile(new URL('../src/assets/examples/Example01_Source_Mirror_Probe.json', import.meta.url), 'utf8'))
  // The scene from 9656c7d's server check, including absorption (blue) and refraction (red).
  const sceneProject = structuredClone(project)
  Object.assign(sceneProject.scene, { width: 41, height: 23, refractiveIndex: 1.2 })
  sceneProject.parts = [
    { kind: 'Rectangle', x: 17, y: 11, length: 14, width: 10, angle: 15, refractiveIndex: 1.8, absorption: 100, absorptionRamp: 2 },
    { kind: 'Ellipse', x: 28, y: 11, length: 10, width: 8, enabled: false },
  ]
  const geometry = sceneKey(sceneProject)
  const painted = decodeScene(runtime.RenderScene(geometry))
  assert.ok(painted)
  assert.deepEqual([painted.width, painted.height], [41, 23])
  assert.equal(painted.image.length, 41 * 23 * 4)
  // SHA-256 of native PartPainter.Paint / GlowingEdges.Compute output for the same model,
  // converted from BGRA to RGBA. These also catch channel swaps and Wasm SIMD differences.
  const hash = bytes => createHash('sha256').update(bytes).digest('hex')
  assert.equal(hash(painted.image), 'eb2faae951b3df02229c2eea7e0a736ed70941e439ea6260927e76d0b078ab8c', 'Scene pixels match native .NET byte for byte')
  assert.equal(hash(painted.edges), 'ff46aa55c7d0c1b838417a7b3989868831f1a0f42806708c7be72661b93d6f4c', 'Glowing edges match native .NET byte for byte')
  assert.ok(painted.edges.some((value, i) => i % 4 !== 3 && value > 0), 'Painted geometry has visible edges')
  sceneProject.display.field.overlay = false
  sceneProject.display.intensity.overlayGain = 0.5
  sceneProject.sources[0].x++
  sceneProject.parts[0].id = crypto.randomUUID()
  sceneProject.parts[0].name = 'Renamed'
  assert.equal(sceneKey(sceneProject), geometry, 'Source, display, identity and name edits reuse scene geometry')
  assert.deepEqual(runtime.RenderScene(JSON.stringify(sceneProject)), runtime.RenderScene(geometry), 'Only geometry affects scene pixels')
  sceneProject.parts[0].x += 8
  const moved = decodeScene(runtime.RenderScene(sceneKey(sceneProject)))
  assert.notDeepEqual(moved.edges, painted.edges, 'Geometry edits repaint the outline')
  sceneProject.parts[0].enabled = false
  assert.equal(decodeScene(runtime.RenderScene(sceneKey(sceneProject))), null, 'An unpainted canvas has no scene layers')
  sceneProject.parts = []
  Object.assign(sceneProject.scene, { width: 1, height: 0 })
  const empty = runtime.RenderScene(sceneKey(sceneProject))
  assert.equal(empty.length, 8, 'An empty canvas transfers only dimensions')
  assert.deepEqual(Array.from(new Int32Array(empty.buffer, empty.byteOffset, 2)), [8, 8], 'The shared model normalises scene dimensions')
  sceneProject.scene.mode = 'Image'
  assert.throws(() => runtime.RenderScene(sceneKey(sceneProject)), /Image scenes are not supported/, 'Image scene previews fail explicitly')
  assert.throws(() => decodeScene(new Uint8Array(7)), /incomplete scene/)
  assert.throws(() => decodeScene(new Uint8Array([...empty, 0])), /incomplete scene layers/)
  const mirror = { ...project.parts[0], x: 32, y: 24, length: 8, width: 48, angle: 0 }
  Object.assign(project.scene, { width: 64, height: 48, dampingEdge: -8 })
  Object.assign(project.sources[0], { x: 32, y: 24 })
  project.parts = []
  project.probes = []
  project.simulation.steps = 9
  const json = JSON.stringify(project)
  const simulate = input => {
    try {
      const metadata = Array.from(runtime.StartSimulation(input, 100))
      assert.deepEqual(metadata, [64, 48, 9, 2, 5], 'The preview preserves the requested interval when storage permits')
      const frames = Array.from({ length: metadata[4] }, () => runtime.NextFrame())
      assert.throws(() => runtime.NextFrame(), /no more preview frames/, 'The export enforces the frame limit')
      for (const frame of frames) {
        assert.equal(frame.byteLength, 64 * 48 * 4, 'Each frame contains two half-float planes')
        const samples = new Uint16Array(frame.buffer, frame.byteOffset, frame.byteLength / 2)
        assert.ok(samples.every(value => (value & 0x7c00) !== 0x7c00), 'All field and intensity samples are finite')
      }
      return frames.at(-1)
    } finally {
      runtime.EndSimulation()
    }
  }
  const frame = simulate(json)
  assert.ok(frame.subarray(0, 64 * 48 * 2).some(value => value !== 0), 'The submitted source produces a field')
  assert.ok(frame.subarray(64 * 48 * 2).some(value => value !== 0), 'The submitted source produces intensity')
  assert.deepEqual(simulate(json), frame, 'A new simulation reproduces the same project')
  runtime.StartSimulation(json, 100)
  runtime.RenderScene(geometry)
  for (let i = 0; i < 4; i++) runtime.NextFrame()
  assert.deepEqual(runtime.NextFrame(), frame, 'Rendering a scene neither starts nor interrupts a simulation')
  runtime.EndSimulation()
  project.parts = [mirror]
  const reflected = simulate(JSON.stringify(project))
  assert.notDeepEqual(reflected, frame, 'Optical parts are deserialised and painted into the simulated scene')
  project.parts[0].enabled = false
  assert.deepEqual(simulate(JSON.stringify(project)), frame, 'Disabled optical parts leave the field unchanged')
  project.parts = []
  project.sources[0].amplitude = 0
  assert.ok(simulate(JSON.stringify(project)).every(value => value === 0), 'Editing the submitted source changes the result')
  runtime.EndSimulation()
  assert.throws(() => runtime.NextFrame(), /no more preview frames/, 'A released simulation cannot produce frames')

  assert.deepEqual(Array.from(runtime.StartSimulation(JSON.stringify(project), 3)), [64, 48, 7, 3, 3], 'A smaller budget increases the interval across the requested run')
  runtime.EndSimulation()
  project.simulation.steps = 1
  assert.equal(runtime.StartSimulation(JSON.stringify(project), 100)[4], 1, 'Short projects only produce their requested frames')
  runtime.NextFrame()
  assert.throws(() => runtime.NextFrame(), /no more preview frames/)
  runtime.EndSimulation()
  project.scene.mode = 'Image'
  assert.throws(() => runtime.StartSimulation(JSON.stringify(project), 100), /Image scenes are not supported/, 'Unsupported scenes fail explicitly')
  console.log(`Wasm scene painting, glowing edges, solver, project input and bounded frame exports passed (peak field amplitude ${peak}).`)
} finally {
  clearInterval(keepAlive)
}
