export interface ProbeFrame {
  stepsSampled: number
  probes: {
    samples: Float32Array<ArrayBuffer>
    profile: Float32Array<ArrayBuffer> | null
    profileLength: number
  }[]
}

/** Decode a recording's incremental point samples and this frame's exact line profiles. */
export function decodeProbeFrame(bytes: Uint8Array<ArrayBuffer>): ProbeFrame {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  if (bytes.byteLength < 8) throw new Error('The probe frame is incomplete.')
  const count = view.getInt32(0, true)
  const stepsSampled = view.getInt32(4, true)
  if (count < 0 || count > (bytes.byteLength - 8) / 16 || stepsSampled < 1) throw new Error('The probe frame has invalid metadata.')
  const probes: ProbeFrame['probes'] = []
  let offset = 8
  for (let i = 0; i < count; i++) {
    if (offset + 16 > bytes.byteLength) throw new Error('The probe frame is incomplete.')
    const samples = view.getInt32(offset, true)
    const profile = view.getInt32(offset + 4, true)
    const profileLength = view.getFloat64(offset + 8, true)
    offset += 16
    const end = offset + (samples + Math.max(0, profile)) * 4
    if (samples < 0 || profile < -1 || end > bytes.byteLength || !Number.isFinite(profileLength) || profileLength < 0)
      throw new Error('The probe frame has invalid samples.')
    probes.push({
      samples: new Float32Array(bytes.buffer, bytes.byteOffset + offset, samples),
      profile: profile < 0 ? null : new Float32Array(bytes.buffer, bytes.byteOffset + offset + samples * 4, profile),
      profileLength,
    })
    offset = end
  }
  if (offset !== bytes.byteLength) throw new Error('The probe frame has unexpected trailing data.')
  return { stepsSampled, probes }
}

/** One growable point history per run; seeking exposes only the selected step's immutable prefix. */
export class ProbeSeries {
  private buffer = new Float32Array(0)
  private count = 0

  append(samples: Float32Array<ArrayBuffer>) {
    if (!samples.length) return
    const count = this.count + samples.length
    if (count > this.buffer.length) {
      const grown = new Float32Array(Math.max(1024, count, this.buffer.length * 2))
      grown.set(this.buffer.subarray(0, this.count))
      this.buffer = grown
    }
    this.buffer.set(samples, this.count)
    this.count = count
  }

  through(steps: number): Float32Array<ArrayBuffer> {
    if (steps > this.count) throw new Error('The point probe history is incomplete.')
    return this.buffer.subarray(0, steps)
  }
}
