import { loadWaveDude } from './Runtime'
import { writeFrames } from './FrameStore'
import { decodeProbeFrame } from './ProbeFrames'
import { prepareRustSimulation } from './PreparedSimulation'
import { loadRustSimulationKernel } from './RustKernel'
import { createRustSimulation } from './RustSimulation'
import type { Project } from '../Model/Project'
import type { RunPerformance } from '../Api/Frames'

export interface SimulationRequest {
  projectJson: string
  runtimeUrl: string
  directory: FileSystemDirectoryHandle
  cancellation?: Int32Array<SharedArrayBuffer>
}

export type SimulationMessage =
  | { type: 'started', width: number, height: number, totalSteps: number, visualizeEvery: number, totalFrames: number, storageBudget: number }
  | { type: 'stored', index: number, probeSamples: Float32Array<ArrayBuffer>[], performance: RunPerformance }
  | { type: 'completed' }
  | { type: 'error', error: string }

const worker = globalThis as unknown as {
  addEventListener: (type: 'message', listener: (event: MessageEvent<SimulationRequest>) => void) => void
  postMessage: (message: SimulationMessage, transfer?: Transferable[]) => void
}

// .NET 10 treats a worker with onmessage set as one of its own runtime threads and hangs
// during startup. A listener leaves onmessage unset: https://github.com/dotnet/runtime/issues/114918
worker.addEventListener('message', async ({ data }) => {
  let simulation: ReturnType<typeof createRustSimulation> | undefined
  try {
    const project = JSON.parse(data.projectJson) as Project
    const { quota, usage } = await navigator.storage.estimate()
    if (quota == null || usage == null || !Number.isFinite(quota) || !Number.isFinite(usage))
      throw new Error('The browser could not estimate available storage.')
    // Leave half the estimated headroom unused. Account for per-file overhead as well as pixels.
    const storageBudget = Math.floor(Math.max(0, quota - usage) / 2)
    const activeProbes = project.probes.filter(probe => probe.active)
    // Probe packets contain per-step point samples and one profile per captured frame. Budget
    // the entire point recording first; it does not shrink when the capture interval grows.
    const pointBytes = activeProbes.filter(probe => probe.kind === 'Point').length * project.simulation.steps * 4
    const profileBytes = activeProbes.filter(probe => probe.kind === 'Line').length * Math.max(project.scene.width, project.scene.height) * 4
    const frameBytes = project.scene.width * project.scene.height * 4 + 8 + activeProbes.length * 16 + profileBytes
    const requestedFrames = Math.ceil(project.simulation.steps / project.simulation.visualizeEvery)
    const maxFrames = Math.min(requestedFrames, Math.floor(Math.max(0, storageBudget - pointBytes) / (frameBytes + 4096)))
    if (maxFrames < 1) throw new Error('There is not enough estimated disc space for one frame.')
    const runtime = await loadWaveDude(data.runtimeUrl)
    const prepared = prepareRustSimulation(runtime, data.projectJson, maxFrames)
    const { width, height, totalSteps, visualizeEvery, totalFrames } = prepared
    const kernel = await loadRustSimulationKernel(prepared, data.cancellation)
    const activeSimulation = createRustSimulation(kernel, prepared)
    simulation = activeSimulation
    const started = performance.now()
    worker.postMessage({ type: 'started', width, height, totalSteps, visualizeEvery, totalFrames, storageBudget })
    await writeFrames(data.directory, totalFrames, async index => {
      const { bytes, probeBytes } = await activeSimulation.nextFrame()
      const probes = decodeProbeFrame(probeBytes)
      const cells = width * height
      if (bytes.byteLength !== cells * 4) throw new Error('The solver returned an incomplete frame.')
      if (probes.probes.length !== prepared.probes.length || probes.stepsSampled !== index * visualizeEvery + 1)
        throw new Error('The solver returned incompatible probe data.')
      const probeSamples = probes.probes.map(probe => probe.samples.slice())
      // A write may finish while the next advance is running. Snapshot only this
      // frame's work here; reading live kernel timings during an advance is invalid.
      return { bytes, probeBytes, probeSamples, stepsSampled: probes.stepsSampled, timings: activeSimulation.timings() }
    }, (index, frame, storageTimings) => {
      const { probeSamples, stepsSampled, timings } = frame
      const elapsedMilliseconds = performance.now() - started
      const workMilliseconds = timings.coordinatorMilliseconds + timings.workerMilliseconds.reduce((sum, value) => sum + value, 0)
      const rates: RunPerformance = { ...timings, storageTimings, elapsedMilliseconds,
        stepsPerSecond: elapsedMilliseconds > 0 ? stepsSampled * 1000 / elapsedMilliseconds : 0,
        parallelism: elapsedMilliseconds > 0 ? workMilliseconds / elapsedMilliseconds : 0 }
      worker.postMessage({ type: 'stored', index, probeSamples, performance: rates }, probeSamples.map(samples => samples.buffer))
    })
    worker.postMessage({ type: 'completed' })
  } catch (error) {
    worker.postMessage({ type: 'error', error: error instanceof Error ? error.message : String(error) })
  } finally {
    simulation?.close()
  }
})
