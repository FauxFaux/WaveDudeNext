import { loadWaveDude } from './Runtime'
import type { FramePair } from '../Api/Frames'

export interface SimulationRequest {
  projectJson: string
  runtimeUrl: string
  runId: string
}

export type SimulationMessage =
  | { type: 'started', width: number, height: number, totalSteps: number, visualizeEvery: number, totalFrames: number }
  | { type: 'frame', frame: FramePair }
  | { type: 'completed' }
  | { type: 'error', error: string }

const worker = globalThis as unknown as {
  addEventListener: (type: 'message', listener: (event: MessageEvent<SimulationRequest>) => void) => void
  postMessage: (message: SimulationMessage, transfer?: Transferable[]) => void
}

// .NET 10 treats a worker with onmessage set as one of its own runtime threads and hangs
// during startup. A listener leaves onmessage unset: https://github.com/dotnet/runtime/issues/114918
worker.addEventListener('message', async ({ data }) => {
  let runtime
  try {
    runtime = await loadWaveDude(data.runtimeUrl)
    const [width, height, totalSteps, visualizeEvery, totalFrames] = runtime.StartSimulation(data.projectJson)
    worker.postMessage({ type: 'started', width, height, totalSteps, visualizeEvery, totalFrames })
    for (let index = 0; index < totalFrames; index++) {
      const bytes = runtime.NextFrame()
      const cells = width * height
      if (bytes.byteLength !== cells * 4) throw new Error('The solver returned an incomplete frame.')
      const samples = new Uint16Array(bytes.buffer, bytes.byteOffset, cells * 2)
      const frame: FramePair = {
        runId: data.runId, projectRevision: 1, index,
        field: { width, height, kind: 'field', samples: samples.subarray(0, cells) },
        intensity: { width, height, kind: 'intensity-root', samples: samples.subarray(cells) },
      }
      worker.postMessage({ type: 'frame', frame }, [bytes.buffer])
    }
    worker.postMessage({ type: 'completed' })
  } catch (error) {
    worker.postMessage({ type: 'error', error: error instanceof Error ? error.message : String(error) })
  } finally {
    runtime?.EndSimulation()
  }
})
