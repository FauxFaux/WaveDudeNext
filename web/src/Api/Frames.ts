import { z } from 'zod'
import { ProbeKindSchema, ProbeMeasurementSchema } from './Project.generated'
import type { RunStatus } from './Workspace'

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
export type FrameProbe = z.infer<typeof FrameProbeSchema>

const FrameProbesSchema = z.strictObject({
  runId: z.uuid(),
  projectRevision: z.number().int().min(1),
  index: z.number().int().min(0),
  step: z.number().int().min(0),
  probesStale: z.boolean(),
  probes: z.array(FrameProbeSchema),
})
export type FrameProbes = z.infer<typeof FrameProbesSchema>

/** A 409 means the requested frame or its exact probe recordings are not available yet. */
export class FrameRequestError extends Error {
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'FrameRequestError'
    this.status = status
  }
}

/** Point values start at step 1; line profiles belong to this exact frame. Null samples are graph gaps.
 * Measurement settings describe the original run; join probeId to the project for graph placement/scaling.
 */
export async function fetchFrameProbes(workspaceId: string, run: RunStatus, index: number, signal: AbortSignal): Promise<FrameProbes> {
  const response = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/runs/${run.runId}/frames/${index}/probes`, { signal })
  if (!response.ok) throw new FrameRequestError(`Could not load frame probes (HTTP ${response.status}).`, response.status)
  const result = FrameProbesSchema.safeParse(await response.json())
  if (!result.success) throw new Error('The server returned incompatible probe data.')
  const data = result.data
  const step = index * run.visualizeEvery
  if (data.runId !== run.runId || data.projectRevision !== run.projectRevision || data.index !== index || data.step !== step
    || new Set(data.probes.map(probe => probe.probeId)).size !== data.probes.length
    || data.probes.some(probe => probe.stepsSampled !== step + 1 || (probe.kind === 'Point'
      ? probe.values.length !== step + 1 || probe.profile !== null || probe.profileLength !== 0
      : probe.values.length !== 0 || probe.profile === null))) {
    throw new Error('The server returned incompatible probe metadata.')
  }
  return data
}

async function fetchPlane(workspaceId: string, run: RunStatus, index: number, layer: FrameLayer, signal: AbortSignal): Promise<FramePlane> {
  const response = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/runs/${run.runId}/frames/${index}/${layer}`, { signal })
  if (!response.ok) throw new FrameRequestError(`Could not load ${layer} frame (HTTP ${response.status}).`, response.status)
  const header = (name: string) => response.headers.get(name)
  const kind = header('X-Frame-Sample-Kind')
  if (header('X-Frame-Format') !== 'float16-le' || header('X-Run-Id') !== run.runId
    || header('X-Frame-Index') !== String(index) || Number(header('X-Project-Revision')) !== run.projectRevision
    || Number(header('X-Frame-Width')) !== run.width || Number(header('X-Frame-Height')) !== run.height
    || header('X-Frame-Step') !== String(index * run.visualizeEvery)
    || (layer === 'field' ? kind !== 'field' : kind !== 'intensity' && kind !== 'intensity-root')) {
    throw new Error('The server returned incompatible frame metadata.')
  }
  const buffer = await response.arrayBuffer()
  if (buffer.byteLength !== run.width * run.height * 2) throw new Error('The frame buffer has an unexpected length.')
  const samples = new Uint16Array(buffer)
  // The wire is always little-endian; Uint16Array uses the browser's native order.
  if (new Uint8Array(new Uint16Array([1]).buffer)[0] !== 1) {
    for (let i = 0; i < samples.length; i++) samples[i] = (samples[i] >>> 8) | (samples[i] << 8)
  }
  return { width: run.width, height: run.height, kind: kind as SampleKind, samples }
}

/** Opt in to fetching probes with both planes; all results are checked against the same run/frame identity. */
export async function fetchFramePair(workspaceId: string, run: RunStatus, index: number, signal: AbortSignal, options: { includeProbes?: boolean } = {}): Promise<FramePair> {
  const [field, intensity, probes] = await Promise.all([
    fetchPlane(workspaceId, run, index, 'field', signal),
    run.hasIntensity ? fetchPlane(workspaceId, run, index, 'intensity', signal) : undefined,
    options.includeProbes ? fetchFrameProbes(workspaceId, run, index, signal) : undefined,
  ])
  return { runId: run.runId, projectRevision: run.projectRevision, index, field, intensity, probes }
}
