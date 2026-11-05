export type SourceMode = 'Continuous' | 'Pulsed'
export type PulseShape = 'Gaussian' | 'SmoothedSquare'

// Persisted settings from Model/Source.cs. Generated emitters belong to the backend.
export interface SourceArray {
  sizeX: number
  sizeY: number
  spacingX: number
  spacingY: number
  angle: number
  randomFrequency: number
  randomPhase: number
  randomOffWidth: number
  randomPosition: number
}

export interface Source {
  name: string
  enabled: boolean
  /** Position in canvas pixels. */
  x: number
  y: number
  /** Editor level, 0.01–255. */
  frequency: number
  /** Percentage, 0–100. */
  amplitude: number
  /** Degrees, -180–180. */
  phase: number
  mode: SourceMode
  pulseShape: PulseShape
  /** Pulse and gap lengths in cycles. */
  pulseWidth: number
  offWidth: number
  /** Simulation step; -1 keeps the source attached. */
  detachAfter: number
  array: SourceArray | null
}

export function createSource(name: string, x: number, y: number): Source {
  return {
    name, enabled: true, x, y, frequency: 100, amplitude: 100, phase: 0,
    mode: 'Continuous', pulseShape: 'Gaussian', pulseWidth: 10, offWidth: 10,
    detachAfter: -1, array: null,
  }
}
