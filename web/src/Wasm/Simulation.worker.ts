import { loadWaveDude } from './Runtime'
import { writeFrame } from './FrameStore'
import type { Project } from '../Model/Project'

export interface SimulationRequest {
  projectJson: string
  runtimeUrl: string
  directory: FileSystemDirectoryHandle
}

export type SimulationMessage =
  | { type: 'started', width: number, height: number, totalSteps: number, visualizeEvery: number, totalFrames: number, storageBudget: number }
  | { type: 'stored', index: number }
  | { type: 'completed' }
  | { type: 'error', error: string }

const worker = globalThis as unknown as {
  addEventListener: (type: 'message', listener: (event: MessageEvent<SimulationRequest>) => void) => void
  postMessage: (message: SimulationMessage) => void
}

// .NET 10 treats a worker with onmessage set as one of its own runtime threads and hangs
// during startup. A listener leaves onmessage unset: https://github.com/dotnet/runtime/issues/114918
worker.addEventListener('message', async ({ data }) => {
  let runtime
  try {
    const project = JSON.parse(data.projectJson) as Project
    const { quota, usage } = await navigator.storage.estimate()
    if (quota == null || usage == null || !Number.isFinite(quota) || !Number.isFinite(usage))
      throw new Error('The browser could not estimate available storage.')
    // Leave half the estimated headroom unused. Account for per-file overhead as well as pixels.
    const storageBudget = Math.floor(Math.max(0, quota - usage) / 2)
    const frameBytes = project.scene.width * project.scene.height * 4
    const requestedFrames = Math.ceil(project.simulation.steps / project.simulation.visualizeEvery)
    const maxFrames = Math.min(requestedFrames, Math.floor(storageBudget / (frameBytes + 4096)))
    if (maxFrames < 1) throw new Error('There is not enough estimated disc space for one frame.')
    runtime = await loadWaveDude(data.runtimeUrl)
    const [width, height, totalSteps, visualizeEvery, totalFrames] = runtime.StartSimulation(data.projectJson, maxFrames)
    worker.postMessage({ type: 'started', width, height, totalSteps, visualizeEvery, totalFrames, storageBudget })
    for (let index = 0; index < totalFrames; index++) {
      const bytes = runtime.NextFrame()
      const cells = width * height
      if (bytes.byteLength !== cells * 4) throw new Error('The solver returned an incomplete frame.')
      await writeFrame(data.directory, index, bytes)
      worker.postMessage({ type: 'stored', index })
    }
    worker.postMessage({ type: 'completed' })
  } catch (error) {
    worker.postMessage({ type: 'error', error: error instanceof Error ? error.message : String(error) })
  } finally {
    runtime?.EndSimulation()
  }
})
