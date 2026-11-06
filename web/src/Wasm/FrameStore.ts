import type { FramePair } from '../Api/Frames'
import type { Probe } from '../Api/Project.generated'
import { decodeProbeFrame } from './ProbeFrames.ts'
import type { ProbeSeries } from './ProbeFrames'

const directoryName = 'wavedude-frames'

/** Temporary recordings are isolated by run so separate tabs do not overwrite each other. */
export async function createFrameStore(runId: string): Promise<FileSystemDirectoryHandle> {
  const root = await navigator.storage.getDirectory()
  const recordings = await root.getDirectoryHandle(directoryName, { create: true })
  return recordings.getDirectoryHandle(runId, { create: true })
}

export async function deleteFrameStore(runId: string): Promise<void> {
  try {
    const root = await navigator.storage.getDirectory()
    const recordings = await root.getDirectoryHandle(directoryName)
    await recordings.removeEntry(runId, { recursive: true })
  } catch (error) {
    if (!(error instanceof DOMException && error.name === 'NotFoundError')) throw error
  }
}

/** Closing each file publishes it before the worker announces the frame to the UI. */
export async function writeFrame(directory: FileSystemDirectoryHandle, index: number, bytes: Uint8Array<ArrayBuffer>, probes: Uint8Array<ArrayBuffer>): Promise<void> {
  const file = await directory.getFileHandle(`${index}.bin`, { create: true })
  const writable = await file.createWritable()
  try {
    await writable.write(new Blob([bytes, probes]))
    await writable.close()
  } catch (error) {
    await writable.abort().catch(() => {})
    throw error
  }
}

/** Overlap production with at most two writes, publishing closed files in order.
 * The producer transfers ownership of each frame's buffers until its notification.
 * Attach failure handlers immediately and settle outstanding writes before returning.
 */
export async function writeFrames<T extends { bytes: Uint8Array<ArrayBuffer>, probeBytes: Uint8Array<ArrayBuffer> }>(
  directory: FileSystemDirectoryHandle,
  count: number,
  nextFrame: (index: number) => Promise<T>,
  onStored: (index: number, frame: T, timings: { writeMilliseconds: number, capacityWaitMilliseconds: number }) => void,
): Promise<void> {
  const pending: Promise<void>[] = []
  let publication = Promise.resolve()
  let failure: { error: unknown } | undefined
  let writeMilliseconds = 0, capacityWaitMilliseconds = 0
  const failed = (error: unknown) => { failure ??= { error } }
  const check = () => { if (failure) throw failure.error }
  try {
    for (let index = 0; index < count; index++) {
      // Make room before producing another owned frame, keeping memory bounded.
      if (pending.length === 2) {
        const started = performance.now()
        await pending.shift()
        capacityWaitMilliseconds += performance.now() - started
      }
      check()
      const frame = await nextFrame(index)
      check()
      const started = performance.now()
      const written = writeFrame(directory, index, frame.bytes, frame.probeBytes)
        .then(() => performance.now() - started).catch(error => { failed(error); return 0 })
      publication = publication.then(async () => {
        writeMilliseconds += await written
        if (!failure) onStored(index, frame, { writeMilliseconds, capacityWaitMilliseconds })
      }).catch(failed)
      pending.push(publication)
    }
    await Promise.all(pending)
    check()
  } finally {
    await Promise.all(pending)
  }
}

export async function readFrame(directory: FileSystemDirectoryHandle, runId: string, index: number, width: number, height: number,
  probes: readonly Probe[], series: readonly ProbeSeries[], visualizeEvery: number): Promise<FramePair> {
  const handle = await directory.getFileHandle(`${index}.bin`)
  const file = await handle.getFile()
  const cells = width * height
  if (file.size < cells * 4 + 8) throw new Error('The stored frame is incomplete.')
  const bytes = await file.arrayBuffer()
  const samples = new Uint16Array(bytes, 0, cells * 2)
  const recorded = decodeProbeFrame(new Uint8Array(bytes, cells * 4))
  if (recorded.probes.length !== probes.length || recorded.stepsSampled !== index * visualizeEvery + 1)
    throw new Error('The stored probe frame is incompatible with the recording.')
  return {
    runId, projectRevision: 1, index,
    field: { width, height, kind: 'field', samples: samples.subarray(0, cells) },
    intensity: { width, height, kind: 'intensity-root', samples: samples.subarray(cells) },
    probes: {
      runId, projectRevision: 1, index, step: index * visualizeEvery, probesStale: false,
      probes: probes.map((probe, i) => ({
        probeId: probe.id, name: probe.name, kind: probe.kind, measurement: probe.measurement,
        ax: probe.ax, ay: probe.ay, bx: probe.bx, by: probe.by, startAfter: probe.startAfter,
        values: probe.kind === 'Point' ? series[i].through(recorded.stepsSampled) : new Float32Array(0),
        profile: recorded.probes[i].profile, profileLength: recorded.probes[i].profileLength,
        stepsSampled: recorded.stepsSampled,
      })),
    },
  }
}
