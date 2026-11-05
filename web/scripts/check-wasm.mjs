import assert from 'node:assert/strict'
import { loadWaveDude } from '../public/wasm/main.mjs'

// The .NET Node host may unref its startup work; keep Node alive until the check finishes.
const keepAlive = setInterval(() => {}, 1000)
try {
  const runtime = await loadWaveDude()
  assert.equal(await loadWaveDude(), runtime, 'The host reuses one runtime per JS context')
  const peak = runtime.SmokeTest()
  assert.ok(Number.isFinite(peak) && peak > 0, 'The shared solver produces a finite nonzero field')
  assert.equal(runtime.SmokeTest(), peak, 'The scene is deterministic across independent runs')
  console.log(`Wasm solver and JavaScript interop passed (peak field amplitude ${peak}).`)
} finally {
  clearInterval(keepAlive)
}
