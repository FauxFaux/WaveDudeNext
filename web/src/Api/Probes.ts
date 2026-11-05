import type { FrameProbe } from './Frames'

export interface ProbeGraphData {
  values?: ArrayLike<number | null>
  count: number
  profile?: ArrayLike<number | null>
  profileLength: number
  stepsSampled: number
}

/** Frame recordings cut histories to the displayed step and supplies its exact line profile. */
export function frameProbeData(recording: FrameProbe | undefined): ProbeGraphData {
  return {
    values: recording?.values,
    count: recording?.values.length ?? 0,
    profile: recording?.profile ?? undefined,
    profileLength: recording?.profileLength ?? 0,
    stepsSampled: recording?.stepsSampled ?? 0,
  }
}
