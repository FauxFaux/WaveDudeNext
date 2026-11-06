import { z } from 'zod'
import { ProbeKindSchema, ProbeMeasurementSchema } from './Project.generated'

export type FrameLayer = 'field' | 'intensity'
export type SampleKind = 'field' | 'intensity' | 'intensity-root'

export interface FramePlane {
  width: number
  height: number
  kind: SampleKind
  samples: Uint16Array<ArrayBuffer>
}

export interface FramePair {
  runId: string
  projectRevision: number
  index: number
  field: FramePlane
  intensity?: FramePlane
  probes?: FrameProbes
}

const FrameProbeSchema = z.strictObject({
  probeId: z.uuid(),
  name: z.string(),
  kind: ProbeKindSchema,
  measurement: ProbeMeasurementSchema,
  ax: z.number().int(),
  ay: z.number().int(),
  bx: z.number().int(),
  by: z.number().int(),
  startAfter: z.number().int().min(0),
  values: z.array(z.number().nullable()),
  profile: z.array(z.number().nullable()).min(1).nullable(),
  profileLength: z.number().min(0),
  stepsSampled: z.number().int().min(1),
})
export type FrameProbe = Omit<z.infer<typeof FrameProbeSchema>, 'values' | 'profile'> & {
  values: ArrayLike<number | null>
  profile: ArrayLike<number | null> | null
}

const FrameProbesSchema = z.strictObject({
  runId: z.uuid(),
  projectRevision: z.number().int().min(1),
  index: z.number().int().min(0),
  step: z.number().int().min(0),
  probesStale: z.boolean(),
  probes: z.array(FrameProbeSchema),
})
export type FrameProbes = Omit<z.infer<typeof FrameProbesSchema>, 'probes'> & { probes: FrameProbe[] }

/** Status of the local Wasm recording backed by browser disc storage. */
export interface RunStatus {
  runId: string
  projectRevision: number
  state: 'Starting' | 'Running' | 'Paused' | 'Completed' | 'Cancelled' | 'Failed'
  stepsDone: number
  totalSteps: number
  frameCount: number
  totalFrames: number
  width: number
  height: number
  visualizeEvery: number
  isStale: boolean
  probesStale: boolean
  error: string | null
  hasIntensity: boolean
  storageBudget: number
}
