import { loadWaveDude } from './Runtime'
import { decodeScene } from './Scene'
import type { ScenePixels } from './Scene'

export interface SceneRequest {
  projectJson: string
  runtimeUrl: string
}

export type SceneMessage =
  | { type: 'scene', scene: ScenePixels | null }
  | { type: 'error', error: string }

const worker = globalThis as unknown as {
  addEventListener: (type: 'message', listener: (event: MessageEvent<SceneRequest>) => void) => void
  postMessage: (message: SceneMessage, transfer?: Transferable[]) => void
}

// Leave onmessage unset, as in Simulation.worker, for .NET's independent-worker startup.
worker.addEventListener('message', async ({ data }) => {
  try {
    const runtime = await loadWaveDude(data.runtimeUrl)
    const scene = decodeScene(runtime.RenderScene(data.projectJson))
    worker.postMessage({ type: 'scene', scene }, scene ? [scene.image.buffer] : [])
  } catch (error) {
    worker.postMessage({ type: 'error', error: error instanceof Error ? error.message : String(error) })
  }
})
