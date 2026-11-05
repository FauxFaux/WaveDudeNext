import type { SampleKind } from '../Api/Frames'

export interface GradientStop {
  position: number
  colour: readonly [number, number, number]
}

// Display settings are independent of frame identity and downloads. Scheme controls replace the
// gradient; gain and black level only change uniforms (or the fallback's half-float lookup table).
export interface FrameDisplay {
  brightness: number
  blackLevel: number
  /** Immutable stops in ascending position order; duplicate positions form hard edges. */
  stops: readonly GradientStop[]
}

export const greyStops: readonly GradientStop[] = [
  { position: 0, colour: [0, 0, 0] },
  { position: 1, colour: [254, 254, 254] },
]

export function halfValue(bits: number): number {
  const exponent = (bits >>> 10) & 31
  const fraction = bits & 1023
  const sign = bits & 32768 ? -1 : 1
  return exponent === 31 ? (fraction ? NaN : sign * Infinity)
    : sign * (exponent === 0 ? fraction * 2 ** -24 : (1 + fraction / 1024) * 2 ** (exponent - 15))
}

export function colourPosition(value: number, kind: SampleKind, brightness: number): number {
  if (kind === 'field') {
    const scaled = value * brightness
    return Number.isNaN(scaled) ? 0.5 : (Math.max(-1, Math.min(1, scaled)) + 1) / 2
  }
  if (!(value > 0)) return 0
  return Math.min(1, (kind === 'intensity' ? Math.sqrt(value) : value) * brightness)
}

export function colourAt(position: number, display: FrameDisplay): readonly number[] {
  const stops = display.stops.length ? display.stops : greyStops
  let colour: readonly number[]
  if (position <= stops[0].position) colour = stops[0].colour
  else if (position >= stops.at(-1)!.position) colour = stops.at(-1)!.colour
  else {
    let low = 1
    let high = stops.length - 1
    while (low < high) {
      const middle = (low + high) >>> 1
      if (stops[middle].position < position) low = middle + 1
      else high = middle
    }
    const a = stops[low - 1]
    const b = stops[low]
    const t = (position - a.position) / (b.position - a.position)
    colour = a.colour.map((value, channel) => value + (b.colour[channel] - value) * t)
  }
  return colour.map(channel => Math.round(Math.max(0, Math.min(1,
    channel / 255 * (1 - display.blackLevel) + display.blackLevel)) * 255))
}

export function halfColourTable(kind: SampleKind, display: FrameDisplay): Uint8ClampedArray {
  const table = new Uint8ClampedArray(65536 * 4)
  for (let bits = 0; bits < 65536; bits++) {
    table.set([...colourAt(colourPosition(halfValue(bits), kind, display.brightness), display), 255], bits * 4)
  }
  return table
}
