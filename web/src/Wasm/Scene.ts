import type { Project } from '../Model/Project'

export interface ScenePreview {
  width: number
  height: number
  image: ImageData
  edges: ImageData
}

export interface ScenePixels {
  width: number
  height: number
  image: Uint8ClampedArray<ArrayBuffer>
  edges: Uint8ClampedArray<ArrayBuffer>
}

// Sources, display, timeline, object identity and names do not affect the painted scene.
// This is also the worker input, so only geometry crosses the runtime boundary.
export function sceneKey(project: Project | undefined): string {
  return project ? JSON.stringify({ scene: project.scene,
    parts: project.parts.map(({ id: _id, name: _name, ...part }) => part) }) : ''
}

export function decodeScene(bytes: Uint8Array<ArrayBuffer>): ScenePixels | null {
  if (bytes.byteLength < 8) throw new Error('The painter returned an incomplete scene.')
  const header = new DataView(bytes.buffer, bytes.byteOffset, 8)
  const width = header.getInt32(0, true)
  const height = header.getInt32(4, true)
  if (width < 1 || height < 1) throw new Error('The painter returned invalid scene dimensions.')
  if (bytes.byteLength === 8) return null
  const size = width * height * 4
  if (bytes.byteLength !== 8 + size * 2) throw new Error('The painter returned incomplete scene layers.')
  // Both views share one transferable buffer; no PNG encoding or decoding is needed locally.
  return { width, height,
    image: new Uint8ClampedArray(bytes.buffer, bytes.byteOffset + 8, size),
    edges: new Uint8ClampedArray(bytes.buffer, bytes.byteOffset + 8 + size, size) }
}
