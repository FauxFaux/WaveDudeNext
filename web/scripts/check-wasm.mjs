import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { loadWaveDude } from '../public/wasm/main.mjs'

// The .NET Node host may unref its startup work; keep Node alive until the check finishes.
const keepAlive = setInterval(() => {}, 1000)
try {
  const runtime = await loadWaveDude()
  assert.equal(await loadWaveDude(), runtime, 'The host reuses one runtime per JS context')
  const peak = runtime.SmokeTest()
  assert.ok(Number.isFinite(peak) && peak > 0, 'The shared solver produces a finite nonzero field')
  assert.equal(runtime.SmokeTest(), peak, 'The scene is deterministic across independent runs')

  const project = JSON.parse(await readFile(new URL('../src/assets/examples/Example01_Source_Mirror_Probe.json', import.meta.url), 'utf8'))
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
  console.log(`Wasm solver, project input and bounded frame exports passed (peak field amplitude ${peak}).`)
} finally {
  clearInterval(keepAlive)
}
