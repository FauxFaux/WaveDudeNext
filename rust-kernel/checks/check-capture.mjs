// SPDX-License-Identifier: GPL-3.0-or-later

import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { prepareKernel } from '../interface.mjs'

// Node 24's native half conversion is independent of Rust's SIMD/scalar paths.
const halfBits = Uint16Array.from({ length: 65536 }, (_, i) => i)
const halfValues = new Float16Array(halfBits.buffer)
const bits = new Uint32Array(1), floats = new Float32Array(bits.buffer)
const expectedHalf = new Float16Array(1), expectedBits = new Uint16Array(expectedHalf.buffer)
const pattern = value => { floats[0] = value; return bits[0] }
const values = []
for (let i = 0; i < 65536; i++) {
  if ((i & 0x7c00) !== 0x7c00) values.push(pattern(halfValues[i]))
}
for (let i = 0; i < 0x7bff; i++) {
  const middle = pattern((halfValues[i] + halfValues[i + 1]) / 2)
  for (const adjacent of [middle - 1, middle, middle + 1]) {
    values.push(adjacent, (adjacent | 0x80000000) >>> 0)
  }
}
values.push(0, 0x80000000, pattern(65519), pattern(65520), pattern(-65520),
  0x7f800000, 0xff800000, 0x7f800001, 0xff800001, 0x7fffffff, 0xffffffff)
let seed = 17239
for (let i = 0; i < 65536; i++) {
  seed ^= seed << 13; seed ^= seed >>> 17; seed ^= seed << 5
  values.push(seed >>> 0)
}

// A source/probe-free preparation packet keeps this check independent of .NET.
function packet(width, height, rim) {
  const align = value => Math.ceil(value / 16) * 16
  const gridWidth = width + 2 * rim, gridHeight = height + 2 * rim
  const stride = gridWidth + 2, cells = stride * (gridHeight + 2)
  const damping = align(160 + cells * 4), end = align(damping + cells * 4)
  const bytes = new Uint8Array(end), header = new DataView(bytes.buffer)
  bytes.set(new TextEncoder().encode('WDPK'))
  for (const [at, value] of [[4, 1], [8, end], [12, 160], [16, width], [20, height],
    [24, gridWidth], [28, gridHeight], [32, rim], [36, stride], [40, cells], [44, 4],
    [48, 1], [52, 1], [56, 1], [60, 1], [64, 1], [80, 160], [84, damping],
    [88, end], [92, end], [96, end], [144, 64], [148, 32], [152, 30]]) {
    header.setUint32(at, value, true)
  }
  for (const [at, value] of [[104, 1], [108, .9], [112, 1], [116, .9], [120, .5], [124, .5621388]])
    header.setFloat32(at, value, true)
  header.setFloat64(136, 10, true)
  return bytes
}

let checked = 0
for (const shared of [false, true]) {
  const name = shared ? 'rust-kernel-shared.wasm' : 'rust-kernel.wasm'
  const module = await WebAssembly.compile(await readFile(new URL(`../../web/src/assets/${name}`, import.meta.url)))
  const memory = shared ? new WebAssembly.Memory({ initial: 32, maximum: 32768, shared: true }) : undefined
  const { exports } = await WebAssembly.instantiate(module, shared ? { env: { memory } } : {})
  const kernel = shared ? { ...exports, memory } : exports
  // Tiny widths exercise scalar-only rows; large widths cover every half and
  // rounding midpoint through SIMD, with each possible tail and unaligned row.
  for (const width of [1, 2, 3, 4, 5, 257, 258, 259, 260]) {
    const height = width < 10 ? 3 : Math.ceil(values.length / width), rim = 2
    const stride = width + 2 * rim + 2, cells = width * height
    const run = prepareKernel(kernel, packet(width, height, rim))
    run.capture() // Finish memory growth before acquiring working-state views.
    const views = run.views()
    views.field0.fill(123)
    views.field1.fill(-123)
    views.intensityOutput.fill(456)
    const fieldBits = [views.field0, views.field1].map(field => new Uint32Array(field.buffer, field.byteOffset, field.length))
    const intensityBits = new Uint32Array(views.intensityOutput.buffer, views.intensityOutput.byteOffset, views.intensityOutput.length)
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const i = y * width + x, at = (y + rim + 1) * stride + rim + 1 + x
      fieldBits[0][at] = values[i % values.length]
      fieldBits[1][at] = values[(i + 17) % values.length]
      intensityBits[at] = values[(i * 7) % values.length] & 0x7fffffff
    }
    for (const slot of [0, 1]) {
      views.control[1] = slot
      const before = new Uint8Array(kernel.memory.buffer, run.stateAddress, run.layout.stateBytes).slice()
      const frame = run.capture(), output = new DataView(frame.buffer, frame.byteOffset, frame.byteLength)
      for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        const i = y * width + x, at = (y + rim + 1) * stride + rim + 1 + x
        const source = fieldBits[slot][at]
        expectedHalf[0] = slot === 0 ? views.field0[at] : views.field1[at]
        const fieldHalf = (source & 0x7fffffff) > 0x7f800000
          ? ((source >>> 16) & 0x8000) | 0x7e00 | ((source & 0x7fffff) >>> 13)
          : expectedBits[0]
        assert.equal(output.getUint16(i * 2, true), fieldHalf, `field: ${name}, width=${width}, slot=${slot}, pixel=${i}`)
        const root = Math.fround(Math.sqrt(views.intensityOutput[at]))
        const actual = output.getUint16((cells + i) * 2, true)
        if (Number.isNaN(root)) {
          // Wasm sqrt may choose a NaN payload; the result must remain a NaN.
          assert.equal(actual & 0x7c00, 0x7c00)
          assert.notEqual(actual & 0x3ff, 0)
        } else {
          expectedHalf[0] = root
          assert.equal(actual, expectedBits[0], `sqrt(I): ${name}, width=${width}, slot=${slot}, pixel=${i}`)
        }
        checked += 2
      }
      assert.deepEqual(new Uint8Array(kernel.memory.buffer, run.stateAddress, run.layout.stateBytes), before)
    }
  }
}
console.log(`Capture conversion passed: ${checked} samples; all finite halves, rounding midpoints, random bits, NaNs, tails, rims and field slots in ordinary/shared Wasm.`)
