import type { WaveDudeRuntime } from './Runtime'

export interface PreparedEmitter {
  readonly index: number
  /** Numeric C# modes: continuous = 0, pulsed = 1; Gaussian = 0, smoothed square = 1. */
  readonly mode: 0 | 1
  readonly pulseShape: 0 | 1
  readonly rngSeed: number
  readonly onWidthCycles: number
  readonly initialOffWidthCycles: number
  readonly offWidthBase: number
  readonly randomOffWidthAmount: number
  readonly detachAfterStep: number
  /** Radians per simulation step, already resolved by C#. */
  readonly omega: number
  readonly amplitude: number
  readonly phase: number
}

export interface PreparedProbe {
  readonly pixels: Uint32Array<ArrayBuffer>
  readonly isProfile: boolean
  readonly useIntensity: boolean
  readonly cumulative: boolean
  readonly integrate: boolean
  readonly useStartAfter: boolean
  readonly startAfter: number
  readonly length: number
  /** Position in the original project's complete probe list, including disabled entries. */
  readonly projectIndex: number
}

/** Immutable run inputs, retained in the coordinator until it terminates. Treat typed arrays as read-only. */
export interface PreparedSimulation {
  /** Owned WDPK v1 bytes; offsets below are relative to this view, never .NET or Rust pointers. */
  readonly packet: Uint8Array<ArrayBuffer>
  readonly offsets: Readonly<{ c2: number, damping: number, emitters: number, probes: number, pixels: number }>
  readonly width: number
  readonly height: number
  readonly gridWidth: number
  readonly gridHeight: number
  readonly rim: number
  readonly stride: number
  readonly cellCount: number
  readonly openEdges: boolean
  readonly fourthOrder: boolean
  readonly trackIntensity: boolean
  readonly subSteps: number
  readonly requestedSteps: number
  readonly visualizeEvery: number
  readonly totalFrames: number
  readonly totalSteps: number
  readonly c2Multiplier: number
  readonly intensityAlpha: number
  readonly intensityScale: number
  readonly sourceOpacity: number
  readonly intensityBlend: number
  readonly openEdgeSpeedFactor: number
  readonly sourceFrequencyScale: number
  readonly timeDecay: number
  readonly detachFadeSteps: number
  readonly c2: Float32Array<ArrayBuffer>
  /** Velocity retention, already corrected for substeps; do not take its root again. */
  readonly damping: Float32Array<ArrayBuffer>
  readonly emitters: readonly PreparedEmitter[]
  readonly probes: readonly PreparedProbe[]
}

/** Submit the project once to the C# preparation boundary. No C# engine or Rust state is created. */
export function prepareRustSimulation(runtime: Pick<WaveDudeRuntime, 'Prepare'>, projectJson: string, maxFrames: number): PreparedSimulation {
  return decodePreparedSimulation(runtime.Prepare(projectJson, maxFrames))
}

/** Decode and validate WaveDudeNext.Wasm/PREPARE.md before exposing any numerical inputs. */
export function decodePreparedSimulation(bytes: Uint8Array<ArrayBuffer>): PreparedSimulation {
  const require = (valid: boolean, detail: string) => {
    if (!valid) throw new Error(`Invalid simulation preparation: ${detail}.`)
  }
  require(bytes.byteLength >= 160, 'incomplete header')
  // The export returns owned bytes. Accommodate unaligned views without misaligned typed arrays.
  const packet = bytes.byteOffset % 16 ? bytes.slice() : bytes
  const view = new DataView(packet.buffer, packet.byteOffset, packet.byteLength)
  const u32 = (at: number) => view.getUint32(at, true)
  const i32 = (at: number) => view.getInt32(at, true)
  const f32 = (at: number) => view.getFloat32(at, true)
  const f64 = (at: number) => view.getFloat64(at, true)
  require(u32(0) === 0x4b504457, 'unknown magic')
  require(u32(4) === 1, 'unsupported version')
  require(u32(8) === packet.byteLength && packet.byteLength <= 0x7fffffff && packet.byteLength % 16 === 0 && u32(12) === 160, 'invalid package length')
  require(u32(100) === 0 && u32(156) === 0 && u32(144) === 64 && u32(148) === 32, 'unsupported record layout')

  const width = u32(16), height = u32(20), gridWidth = u32(24), gridHeight = u32(28)
  const rim = u32(32), stride = u32(36), cellCount = u32(40), flags = u32(44), subSteps = u32(48)
  const requestedSteps = u32(52), visualizeEvery = u32(56), totalFrames = u32(60), totalSteps = u32(64)
  const emitterCount = u32(68), probeCount = u32(72), pixelCount = u32(76)
  const offsets = { c2: u32(80), damping: u32(84), emitters: u32(88), probes: u32(92), pixels: u32(96) }
  require(width > 0 && height > 0 && gridWidth === width + 2 * rim && gridHeight === height + 2 * rim
    && stride === gridWidth + 2 && cellCount === stride * (gridHeight + 2), 'inconsistent grid dimensions')
  require((flags & ~7) === 0 && (flags & 4) !== 0 && subSteps > 0, 'unsupported flags or substeps')
  require(requestedSteps > 0 && visualizeEvery > 0 && totalFrames === Math.ceil(requestedSteps / visualizeEvery)
    && totalSteps === (totalFrames - 1) * visualizeEvery + 1, 'inconsistent capture timing')

  let end = 160
  for (const [offset, length] of [[offsets.c2, cellCount * 4], [offsets.damping, cellCount * 4],
    [offsets.emitters, emitterCount * 64], [offsets.probes, probeCount * 32], [offsets.pixels, pixelCount * 4]]) {
    require(offset % 16 === 0 && offset >= end && offset + length <= packet.byteLength, 'invalid section range')
    require(packet.subarray(end, offset).every(value => value === 0), 'nonzero section padding')
    end = offset + length
  }
  require(packet.subarray(end).every(value => value === 0), 'nonzero trailing padding')

  const c2Multiplier = f32(104), intensityAlpha = f32(108), intensityScale = f32(112)
  const sourceOpacity = f32(116), intensityBlend = f32(120), openEdgeSpeedFactor = f32(124)
  const sourceFrequencyScale = f64(128), timeDecay = f64(136), detachFadeSteps = u32(152)
  require(c2Multiplier === Math.fround(1 / (subSteps * subSteps)) && intensityAlpha >= 0 && intensityAlpha <= 1
    && Number.isFinite(intensityScale) && intensityScale >= 0 && sourceOpacity >= 0 && sourceOpacity <= 1
    && intensityBlend >= 0 && intensityBlend <= 1 && Number.isFinite(openEdgeSpeedFactor) && openEdgeSpeedFactor > 0
    && Number.isFinite(sourceFrequencyScale) && sourceFrequencyScale >= 0 && Number.isFinite(timeDecay) && timeDecay >= 0
    && detachFadeSteps > 0, 'invalid numerical constants')

  const c2 = new Float32Array(packet.buffer, packet.byteOffset + offsets.c2, cellCount)
  const damping = new Float32Array(packet.buffer, packet.byteOffset + offsets.damping, cellCount)
  require(c2.every(value => Number.isFinite(value) && value >= 0)
    && damping.every(value => value >= 0 && value <= 1), 'invalid maps')
  const inCanvas = (index: number) => {
    const x = index % stride - rim - 1, y = Math.floor(index / stride) - rim - 1
    return x >= 0 && x < width && y >= 0 && y < height
  }
  const emitters = Array.from({ length: emitterCount }, (_, i): PreparedEmitter => {
    const at = offsets.emitters + i * 64
    const index = u32(at), mode = u32(at + 4), pulseShape = u32(at + 8), rngSeed = u32(at + 12)
    const onWidthCycles = i32(at + 16), initialOffWidthCycles = i32(at + 20), offWidthBase = i32(at + 24)
    const randomOffWidthAmount = i32(at + 28), detachAfterStep = i32(at + 32)
    const omega = f64(at + 40), amplitude = f64(at + 48), phase = f64(at + 56)
    require(inCanvas(index) && mode <= 1 && pulseShape <= 1 && rngSeed > 0 && u32(at + 36) === 0
      && onWidthCycles > 0 && initialOffWidthCycles >= 0 && offWidthBase >= 0 && randomOffWidthAmount >= 0 && detachAfterStep >= -1
      && Number.isFinite(omega) && omega >= 0 && amplitude >= 0 && amplitude <= 1 && Number.isFinite(phase), 'invalid emitter')
    return { index, mode: mode as 0 | 1, pulseShape: pulseShape as 0 | 1, rngSeed, onWidthCycles,
      initialOffWidthCycles, offWidthBase, randomOffWidthAmount, detachAfterStep, omega, amplitude, phase }
  })
  const pixels = new Uint32Array(packet.buffer, packet.byteOffset + offsets.pixels, pixelCount)
  require(pixels.every(inCanvas), 'probe pixel outside canvas')
  let pixelStart = 0, projectIndex = -1
  const probes = Array.from({ length: probeCount }, (_, i): PreparedProbe => {
    const at = offsets.probes + i * 32
    const start = u32(at), count = u32(at + 4), flags = u32(at + 8), startAfter = i32(at + 12)
    const length = f64(at + 16), originalIndex = u32(at + 24)
    require(start === pixelStart && count > 0 && start + count <= pixelCount && (flags & ~31) === 0
      && startAfter >= 0 && Number.isFinite(length) && length >= 0 && originalIndex > projectIndex && u32(at + 28) === 0
      && ((flags & 1) !== 0 || count === 1), 'invalid probe')
    pixelStart += count
    projectIndex = originalIndex
    return { pixels: pixels.subarray(start, start + count), isProfile: !!(flags & 1), useIntensity: !!(flags & 2),
      cumulative: !!(flags & 4), integrate: !!(flags & 8), useStartAfter: !!(flags & 16), startAfter, length, projectIndex }
  })
  require(pixelStart === pixelCount, 'unreferenced probe pixels')
  return { packet, offsets, width, height, gridWidth, gridHeight, rim, stride, cellCount,
    openEdges: !!(flags & 1), fourthOrder: !!(flags & 2), trackIntensity: !!(flags & 4), subSteps,
    requestedSteps, visualizeEvery, totalFrames, totalSteps, c2Multiplier, intensityAlpha, intensityScale,
    sourceOpacity, intensityBlend, openEdgeSpeedFactor, sourceFrequencyScale, timeDecay, detachFadeSteps,
    c2, damping, emitters, probes }
}
