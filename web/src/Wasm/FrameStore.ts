import type { FramePair } from '../Api/Frames'

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
export async function writeFrame(directory: FileSystemDirectoryHandle, index: number, bytes: Uint8Array<ArrayBuffer>): Promise<void> {
  const file = await directory.getFileHandle(`${index}.bin`, { create: true })
  const writable = await file.createWritable()
  try {
    await writable.write(bytes)
    await writable.close()
  } catch (error) {
    await writable.abort().catch(() => {})
    throw error
  }
}

export async function readFrame(directory: FileSystemDirectoryHandle, runId: string, index: number, width: number, height: number): Promise<FramePair> {
  const handle = await directory.getFileHandle(`${index}.bin`)
  const file = await handle.getFile()
  const cells = width * height
  if (file.size !== cells * 4) throw new Error('The stored frame is incomplete.')
  const samples = new Uint16Array(await file.arrayBuffer())
  return {
    runId, projectRevision: 1, index,
    field: { width, height, kind: 'field', samples: samples.subarray(0, cells) },
    intensity: { width, height, kind: 'intensity-root', samples: samples.subarray(cells) },
  }
}
