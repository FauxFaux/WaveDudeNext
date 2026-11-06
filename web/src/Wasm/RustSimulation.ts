import { prepareKernel } from '../../../rust-kernel/interface.mjs'
import type { KernelExports } from '../../../rust-kernel/interface.mjs'
import type { PreparedSimulation } from './PreparedSimulation.ts'

/** Adapt Rust's step-major samples and pixel observations to the existing recording format. */
export function createRustSimulation(exports: KernelExports, prepared: PreparedSimulation) {
  const kernel = prepareKernel(exports, prepared)
  const { probes, visualizeEvery, totalFrames } = prepared
  let index = 0, completed = 0

  return {
    nextFrame() {
      if (index >= totalFrames) throw new Error('There are no more preview frames.')
      // The first frame follows step one; subsequent captures are visualizeEvery steps apart.
      const target = index * visualizeEvery + 1
      const steps = target - completed
      const samples = kernel.advance(steps)
      const profiles = kernel.observe()
      const bytes = kernel.capture()
      const values = probes.reduce((count, probe) => count + (probe.isProfile ? probe.pixels.length : steps), 0)
      const probeBytes = new Uint8Array(8 + probes.length * 16 + values * 4)
      const view = new DataView(probeBytes.buffer)
      view.setInt32(0, probes.length, true)
      view.setInt32(4, target, true)
      let at = 8, pixel = 0
      probes.forEach((probe, i) => {
        const count = probe.isProfile ? probe.pixels.length : steps
        view.setInt32(at, probe.isProfile ? 0 : steps, true)
        view.setInt32(at + 4, probe.isProfile ? count : -1, true)
        view.setFloat64(at + 8, probe.length, true)
        at += 16
        const output = new Float32Array(probeBytes.buffer, at, count)
        if (probe.isProfile) output.set(profiles.subarray(pixel, pixel + count))
        else for (let step = 0; step < steps; step++) output[step] = samples[step * probes.length + i]
        at += count * 4
        pixel += probe.pixels.length
      })
      completed = target
      index++
      return { bytes, probeBytes }
    },
  }
}
