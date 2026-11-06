import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import { loadWaveDude } from '../../web/public/wasm/main.mjs'

// Test/export helper only; no frontend integration or new .NET run API is needed.
const keepAlive = setInterval(() => {}, 1000)
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
const f = Math.fround
const add = (a, b) => f(a + b)
const mul = (a, b) => f(a * b)

function unpack(bytes) {
  assert.ok(bytes instanceof Uint8Array)
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const u32 = offset => view.getUint32(offset, true)
  assert.equal(new TextDecoder().decode(bytes.subarray(0, 4)), 'WDPK')
  assert.equal(u32(4), 1)
  assert.equal(u32(8), bytes.length)
  assert.equal(u32(12), 160)
  assert.equal(bytes.length % 16, 0)
  const names = ['width', 'height', 'gridWidth', 'gridHeight', 'rim', 'stride', 'cells', 'flags', 'subSteps',
    'requestedSteps', 'interval', 'frames', 'lastStep', 'emitterCount', 'probeCount', 'pixelCount',
    'c2Offset', 'dampingOffset', 'emittersOffset', 'probesOffset', 'pixelsOffset']
  const p = Object.fromEntries(names.map((name, i) => [name, u32(16 + i * 4)]))
  for (const [name, offset] of Object.entries({ mult: 104, alpha: 108, intensityScale: 112, opacity: 116, blend: 120, edgeSpeed: 124 }))
    p[name] = view.getFloat32(offset, true)
  p.frequencyScale = view.getFloat64(128, true)
  p.timeDecay = view.getFloat64(136, true)
  assert.equal(p.gridWidth, p.width + p.rim * 2)
  assert.equal(p.gridHeight, p.height + p.rim * 2)
  assert.equal(p.stride, p.gridWidth + 2)
  assert.equal(p.cells, p.stride * (p.gridHeight + 2))
  assert.equal(p.flags & ~7, 0)
  assert.ok(p.flags & 4)
  assert.equal(p.frames, Math.ceil(p.requestedSteps / p.interval))
  assert.equal(p.lastStep, (p.frames - 1) * p.interval + 1)
  assert.equal(u32(100), 0)
  assert.equal(u32(144), 64)
  assert.equal(u32(148), 32)
  assert.equal(u32(152), 30)
  assert.equal(u32(156), 0)
  let end = 160
  for (const [offset, length] of [[p.c2Offset, p.cells * 4], [p.dampingOffset, p.cells * 4],
    [p.emittersOffset, p.emitterCount * 64], [p.probesOffset, p.probeCount * 32], [p.pixelsOffset, p.pixelCount * 4]]) {
    assert.equal(offset % 16, 0)
    assert.ok(offset >= end && offset + length <= bytes.length)
    assert.ok(bytes.subarray(end, offset).every(value => value === 0))
    end = offset + length
  }
  assert.ok(bytes.subarray(end).every(value => value === 0))
  p.c2 = new Float32Array(bytes.buffer, bytes.byteOffset + p.c2Offset, p.cells)
  p.damping = new Float32Array(bytes.buffer, bytes.byteOffset + p.dampingOffset, p.cells)
  p.emitters = Array.from({ length: p.emitterCount }, (_, i) => {
    const at = p.emittersOffset + i * 64
    const record = { index: u32(at), mode: u32(at + 4), shape: u32(at + 8), seed: u32(at + 12),
      on: view.getInt32(at + 16, true), off: view.getInt32(at + 20, true), base: view.getInt32(at + 24, true),
      random: view.getInt32(at + 28, true), detach: view.getInt32(at + 32, true),
      omega: view.getFloat64(at + 40, true), amplitude: view.getFloat64(at + 48, true), phase: view.getFloat64(at + 56, true) }
    assert.equal(u32(at + 36), 0)
    assert.ok(record.index < p.cells && record.seed > 0)
    assert.ok(record.mode <= 1 && record.shape <= 1)
    assert.ok([record.omega, record.amplitude, record.phase].every(Number.isFinite))
    return record
  })
  const pixels = new Uint32Array(bytes.buffer, bytes.byteOffset + p.pixelsOffset, p.pixelCount)
  let start = 0
  p.probes = Array.from({ length: p.probeCount }, (_, i) => {
    const at = p.probesOffset + i * 32
    const flags = u32(at + 8)
    assert.equal(u32(at), start)
    const count = u32(at + 4)
    assert.ok(count > 0 && start + count <= pixels.length)
    const indices = pixels.subarray(start, start + count)
    start += count
    assert.ok(indices.every(index => index < p.cells))
    assert.equal(flags & ~31, 0)
    assert.equal(u32(at + 28), 0)
    return { indices, flags, startAfter: view.getInt32(at + 12, true), length: view.getFloat64(at + 16, true), projectIndex: u32(at + 24) }
  })
  assert.equal(start, p.pixelCount)
  return p
}

function halfValue(bits) {
  const sign = bits & 0x8000 ? -1 : 1
  const exponent = (bits >>> 10) & 31
  const fraction = bits & 1023
  return sign * (exponent ? (1 + fraction / 1024) * 2 ** (exponent - 15) : fraction * 2 ** -24)
}

// A one-step scalar consumer verifies that prepared maps, indices, derived constants
// and source units really reproduce the existing C# engine, rather than just parsing.
function firstStep(p) {
  let current = new Float32Array(p.cells)
  let previous = new Float32Array(p.cells)
  const intensity = new Float32Array(p.cells)
  for (const emitter of p.emitters) {
    assert.equal(emitter.mode, 0)
    const value = f(Math.sin(emitter.phase) * emitter.amplitude)
    current[emitter.index] = add(mul(current[emitter.index], p.opacity), mul(value, f(1 - p.opacity)))
  }
  const sum4 = (a, b, c, d) => add(add(add(a, b), c), d)
  const s = p.stride
  for (let step = 1; step <= p.subSteps; step++) {
    for (let y = 0; y < p.gridHeight; y++) for (let x = 0; x < p.gridWidth; x++) {
      const i = (y + 1) * s + x + 1
      const center = current[i]
      let lap
      if ((p.flags & 2) && x > 0 && x < p.gridWidth - 1 && y > 0 && y < p.gridHeight - 1) {
        lap = add(add(mul(f(-1 / 12), sum4(current[i - 2], current[i + 2], current[i - 2 * s], current[i + 2 * s])),
          mul(f(4 / 3), sum4(current[i - 1], current[i + 1], current[i - s], current[i + s]))), mul(-5, center))
      } else {
        lap = f(add(mul(f(.066), sum4(current[i - s - 1], current[i - s + 1], current[i + s - 1], current[i + s + 1])),
          mul(f(.184), sum4(current[i - 1], current[i + 1], current[i - s], current[i + s]))) - center)
      }
      const next = add(add(center, mul(f(center - previous[i]), p.damping[i])), mul(mul(lap, p.c2[i]), p.mult))
      previous[i] = next
      if (step === p.subSteps) {
        const stage1 = mul(mul(next, next), f(1 - p.alpha))
        const stage2 = mul(stage1, f(1 - p.alpha))
        intensity[i] = mul(add(mul(stage1, f(1 - p.blend)), mul(stage2, p.blend)), p.intensityScale)
      }
    }
    if (p.flags & 1) {
      const absorb = (ring, inner) => {
        const speed = mul(f(Math.sqrt(mul(p.c2[inner], p.mult))), p.edgeSpeed)
        const k = f(f(speed - 1) / add(speed, 1))
        previous[ring] = add(current[inner], mul(k, f(previous[inner] - current[ring])))
      }
      for (let x = 0; x <= p.gridWidth + 1; x++) {
        const ix = Math.min(p.gridWidth, Math.max(1, x))
        absorb(x, s + ix)
        absorb((p.gridHeight + 1) * s + x, p.gridHeight * s + ix)
      }
      for (let y = 1; y <= p.gridHeight; y++) {
        absorb(y * s, y * s + 1)
        absorb(y * s + p.gridWidth + 1, y * s + p.gridWidth)
      }
    }
    ;[current, previous] = [previous, current]
  }
  return { current, intensity }
}

try {
  const runtime = await loadWaveDude()
  assert.equal(typeof runtime.Prepare, 'function', 'Rebuild the Wasm bundle before running this check')
  const exampleUrl = new URL('../../web/src/assets/examples/Example01_Source_Mirror_Probe.json', import.meta.url)
  const exampleJson = await readFile(exampleUrl, 'utf8')
  const example = JSON.parse(exampleJson)
  const small = structuredClone(example)
  Object.assign(small.scene, { width: 13, height: 9, dampingEdge: -2, refractiveIndex: 1.7 })
  Object.assign(small.simulation, { steps: 9, visualizeEvery: 2, intensityScale: 2.5 })
  small.parts = []
  small.sources = [{ ...small.sources[0], x: 2, y: 3, mode: 'Continuous', amplitude: 75, phase: 60 }]
  const point = { ...example.probes[0], ax: 2, ay: 3, bx: 5, by: 3, kind: 'Point', layer: 'Field', measurement: 'Amplitude' }
  small.probes = [
    { ...point, active: false }, point,
    { ...point, layer: 'Intensity', measurement: 'Value' },
    { ...point, layer: 'Intensity', measurement: 'IntegratedValue', startAfter: 2 },
    { ...point, layer: 'Intensity', kind: 'Line', measurement: 'IntegratedValue', startAfter: 2 },
  ]
  for (const fourth of [false, true]) for (const border of [-2, 0, 2]) {
    small.simulation.fourthOrderLaplacian = fourth
    small.scene.dampingEdge = border
    const json = JSON.stringify(small)
    const bytes = runtime.Prepare(json, 3)
    const p = unpack(bytes)
    assert.deepEqual(runtime.Prepare(json, 3), bytes, 'Preparation is deterministic')
    const memory = new WebAssembly.Memory({ initial: Math.ceil((bytes.length + 64) / 65536) })
    const relocated = new Uint8Array(memory.buffer, 64, bytes.length)
    relocated.set(bytes)
    const inWasm = unpack(relocated)
    assert.deepEqual(inWasm.c2, p.c2, 'Maps survive copying to a nonzero base in Wasm linear memory')
    assert.deepEqual(inWasm.emitters, p.emitters, 'Cell indices remain unchanged when the package moves')
    assert.deepEqual([p.interval, p.frames, p.lastStep], [3, 3, 7])
    assert.equal(p.subSteps, fourth ? 2 : 1)
    assert.equal(p.mult, f(1 / p.subSteps ** 2))
    assert.equal(p.alpha, f(Math.exp(-1 / 13)))
    assert.equal(p.emitters[0].omega, f(100 / 255))
    assert.equal(p.emitters[0].amplitude, .75)
    assert.equal(p.emitters[0].phase, Math.PI / 3)
    assert.equal(p.emitters[0].index, (3 + p.rim + 1) * p.stride + 2 + p.rim + 1)
    assert.deepEqual(p.probes.map(probe => probe.projectIndex), [1, 2, 3, 4])
    assert.deepEqual(p.probes.map(probe => probe.flags), [0, 2, 22, 27])
    assert.equal(p.probes[3].length, 3)
    assert.deepEqual(Array.from(p.probes[3].indices), [0, 1, 2, 3].map(dx => p.emitters[0].index + dx))
    for (let y = 0; y < p.gridHeight + 2; y++) for (let x = 0; x < p.stride; x++) {
      const at = y * p.stride + x
      const ring = x === 0 || y === 0 || x === p.stride - 1 || y === p.gridHeight + 1
      assert.equal(p.c2[at], ring ? 0 : mul(f(1 / 1.7), f(1 / 1.7)))
      let d = ring ? 0 : 1
      if (!ring && border) {
        const distance = Math.min(x - 1, y - 1, p.gridWidth - x, p.gridHeight - y)
        if (distance < 2) d = border < 0 ? f(1 - .5 * ((2 - distance) / 2) ** 2) : f(Math.sqrt(distance / 2))
      }
      assert.equal(p.damping[at], p.subSteps > 1 ? f(d ** (1 / p.subSteps)) : d)
    }
    const expected = firstStep(p)
    assert.deepEqual(Array.from(runtime.StartSimulation(json, 3)), [13, 9, p.lastStep, p.interval, p.frames])
    const frame = runtime.NextFrame()
    const half = new DataView(frame.buffer, frame.byteOffset, frame.byteLength)
    for (let y = 0; y < p.height; y++) for (let x = 0; x < p.width; x++) {
      const pixel = y * p.width + x
      const at = (y + p.rim + 1) * p.stride + x + p.rim + 1
      assert.ok(Math.abs(halfValue(half.getUint16(pixel * 2, true)) - expected.current[at]) < 2e-5, 'Prepared inputs reproduce C# field')
      assert.ok(Math.abs(halfValue(half.getUint16(p.width * p.height * 2 + pixel * 2, true)) - Math.sqrt(expected.intensity[at])) < 2e-5, 'Prepared inputs reproduce C# intensity')
    }
    const probePacket = runtime.GetProbeFrame()
    const probeView = new DataView(probePacket.buffer, probePacket.byteOffset, probePacket.byteLength)
    let at = 8
    for (const [i, probe] of p.probes.entries()) {
      const count = probeView.getInt32(at, true)
      if (i < 2) {
        const value = probeView.getFloat32(at + 16, true)
        const expectedValue = (i ? expected.intensity : expected.current)[probe.indices[0]]
        assert.ok(Math.abs(value - expectedValue) < 1e-7, 'Full-precision probes agree before half conversion')
      } else assert.ok(probePacket.subarray(at + 16, at + 16 + (i === 2 ? 1 : 4) * 4).every(value => value === 0), 'Integrated samples respect the start gate')
      at += 16 + (count + Math.max(0, probeView.getInt32(at + 4, true))) * 4
    }
    const untouched = runtime.NextFrame()
    runtime.EndSimulation()
    runtime.StartSimulation(json, 3)
    runtime.NextFrame()
    runtime.Prepare(exampleJson, 2500)
    assert.throws(() => runtime.Prepare(json, 0))
    assert.deepEqual(runtime.NextFrame(), untouched, 'Successful and failed preparation leave an active simulation intact')
    runtime.EndSimulation()
  }
  const filtered = structuredClone(small)
  filtered.sources.push({ ...filtered.sources[0], enabled: false }, { ...filtered.sources[0], x: -1, frequency: 1 },
    { ...filtered.sources[0], y: 9, frequency: 2 })
  assert.equal(unpack(runtime.Prepare(JSON.stringify(filtered), 100)).emitterCount, 1)
  assert.equal(unpack(runtime.Prepare(JSON.stringify(filtered), 100)).alpha, f(Math.exp(-1 / 13)), 'Outside sources cannot change intensity retention')
  filtered.scene.objectSources = false
  filtered.probes = []
  const empty = unpack(runtime.Prepare(JSON.stringify(filtered), 100))
  assert.equal(empty.emitterCount, 0)
  assert.equal(empty.probeCount, 0)
  assert.equal(empty.pixelCount, 0)
  filtered.scene.objectSources = true
  filtered.simulation.sourceFrequencyScale = 0
  const silent = unpack(runtime.Prepare(JSON.stringify(filtered), 100))
  assert.equal(silent.emitters[0].omega, 0)
  assert.equal(silent.alpha, empty.alpha, 'Intensity retention is derived from source levels even at zero frequency scale')
  filtered.scene.mode = 'Image'
  assert.throws(() => runtime.Prepare(JSON.stringify(filtered), 100), /Image scenes are not supported/)
  assert.throws(() => runtime.Prepare(exampleJson, -1))
  assert.throws(() => runtime.Prepare('{', 100))
  const composite = JSON.parse(await readFile(new URL('../../web/src/assets/examples/Example03_Directional_pulsed_source.json', import.meta.url), 'utf8'))
  // Keep the original emitter array and probe kinds, but avoid allocating full-HD maps in a small check.
  Object.assign(composite.scene, { width: 640, height: 640 })
  const expanded = unpack(runtime.Prepare(JSON.stringify(composite), 100))
  assert.equal(expanded.emitterCount, 231)
  assert.equal(expanded.probeCount, 2)
  assert.ok(expanded.emitters.every(emitter => emitter.mode === 1 && emitter.shape === 0 && emitter.amplitude === .7))
  assert.equal(new Set(expanded.emitters.map(emitter => emitter.seed)).size, 231)

  const output = resolve(process.argv[2] ?? '/tmp/wavedude-prepare')
  const packet = runtime.Prepare(exampleJson, 2500)
  const prepared = unpack(packet)
  assert.deepEqual([prepared.width, prepared.height, prepared.rim, prepared.subSteps, prepared.emitterCount, prepared.probeCount], [960, 540, 64, 3, 1, 1])
  assert.ok(prepared.c2.some(value => value > 0 && value < 1), 'The fixture contains the painted mirror')
  assert.equal(prepared.probes[0].flags, 3)
  await mkdir(output, { recursive: true })
  const files = []
  async function save(name, bytes, metadata = {}) {
    await writeFile(resolve(output, name), bytes)
    files.push({ name, bytes: Buffer.byteLength(bytes), sha256: hash(bytes), ...metadata })
  }
  await save('Example01_Source_Mirror_Probe.wdprep', packet)
  await save('Example01_Source_Mirror_Probe.json', exampleJson)
  runtime.StartSimulation(exampleJson, 2500)
  try {
    for (let index = 0; index <= 4; index++) {
      const frame = runtime.NextFrame()
      const probes = runtime.GetProbeFrame()
      if ([0, 1, 4].includes(index)) {
        const metadata = { capture: index, completedSteps: index * prepared.interval + 1 }
        await save(`capture-${index}.frame`, frame, metadata)
        await save(`capture-${index}.probes`, probes, metadata)
      }
      if (index === 4) assert.ok(frame.some(value => value !== 0), 'Reference frames exercise propagation')
    }
  } finally {
    runtime.EndSimulation()
  }
  const { c2, damping, emitters, probes, ...header } = prepared
  const manifest = { format: 'WDPK', version: 1, example: 'web/src/assets/examples/Example01_Source_Mirror_Probe.json',
    maxFrames: 2500, header, emitters, probes: probes.map(({ indices, ...probe }) => ({ ...probe, pixelCount: indices.length })),
    frameFormat: 'Little-endian f16 field plane, then f16 sqrt(intensity) plane; tightly packed canvas rows',
    probeFormat: 'WaveDudeNext.Wasm/ProbeFrames.cs and PREPARE.md; samples are f32 unrooted values', files }
  await writeFile(resolve(output, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n')
  console.log(`Prepare checks passed; example package and C# reference frames written to ${output}`)
} finally {
  clearInterval(keepAlive)
}
