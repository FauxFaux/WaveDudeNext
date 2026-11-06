import { useEffect, useMemo, useRef, useState } from 'preact/hooks'
import type { FramePair, RunStatus } from '../Api/Frames'
import type { Project } from '../Model/Project'
import type { Probe } from '../Api/Project.generated'
import { ProbeSeries } from './ProbeFrames'
import { waveDudeUrl } from './Runtime'
import { createFrameStore, deleteFrameStore, readFrame } from './FrameStore'
import type { SimulationMessage, SimulationRequest } from './Simulation.worker'

interface Recording {
  runId: string
  directory?: FileSystemDirectoryHandle
  probes: readonly Probe[]
  series: ProbeSeries[]
}

/** Keep the selected picture in memory; all recorded frames are read from OPFS. */
export function useSimulation(project: Project) {
  // Presentation edits recolour stored samples without invalidating the worker's input.
  // Probe box/axis settings also belong to presentation (Probe.AffectsMeasurement in the shared model).
  const key = useMemo(() => {
    const { display: _display, colorSchemes: _schemes, ...inputs } = project
    return JSON.stringify({ ...inputs, probes: project.probes.map(({ id, name, active, layer, measurement, kind, ax, ay, bx, by, startAfter }) =>
      ({ id, name, active, layer, measurement, kind, ax, ay, bx, by, startAfter })) })
  }, [project])
  const worker = useRef<Worker | null>(null)
  const cancellation = useRef<Int32Array<SharedArrayBuffer> | null>(null)
  const operation = useRef(0)
  const store = useRef<Recording | null>(null)
  const snapshot = useRef<string | null>(null)
  const currentKey = useRef(key)
  currentKey.current = key
  const [run, setRun] = useState<RunStatus | null>(null)
  const [frame, setFrame] = useState<FramePair>()
  const [position, seek] = useState<number | null>(null)
  const pendingRead = useRef<(() => Promise<void>) | null>(null)
  const reading = useRef(false)
  const readVersion = useRef(0)
  const busy = run?.state === 'Starting' || run?.state === 'Running'

  function release() {
    operation.current++
    if (cancellation.current) Atomics.store(cancellation.current, 0, 1)
    cancellation.current = null
    worker.current?.terminate()
    worker.current = null
  }

  function discard() {
    const previous = store.current
    store.current = null
    readVersion.current++
    pendingRead.current = null
    return previous?.directory ? deleteFrameStore(previous.runId) : Promise.resolve()
  }

  function stop() {
    release()
    setRun(current => current && (current.state === 'Starting' || current.state === 'Running')
      ? { ...current, state: 'Cancelled' } : current)
  }

  function clear() {
    release()
    void discard().catch(console.error)
    snapshot.current = null
    setRun(null)
    setFrame(undefined)
    seek(null)
  }

  async function start() {
    release()
    const previous = discard()
    const version = operation.current
    snapshot.current = key
    setFrame(undefined)
    seek(null)
    const runId = crypto.randomUUID()
    const probes = project.probes.filter(probe => probe.active)
    const recording: Recording = { runId, probes, series: probes.map(() => new ProbeSeries()) }
    store.current = recording
    setRun({
      runId, projectRevision: 1, state: 'Starting', stepsDone: 0, totalSteps: 0,
      frameCount: 0, totalFrames: 0, width: project.scene.width, height: project.scene.height,
      visualizeEvery: project.simulation.visualizeEvery, isStale: false, probesStale: false,
      error: null, hasIntensity: true, storageBudget: 0, performance: null,
    })
    try {
      await previous
      if (operation.current !== version) return
      recording.directory = await createFrameStore(runId)
      if (operation.current !== version || currentKey.current !== key) {
        await deleteFrameStore(runId)
        return
      }
      const next = new Worker(new URL('./Simulation.worker.ts', import.meta.url), { type: 'module' })
      worker.current = next
      cancellation.current = globalThis.crossOriginIsolated && typeof SharedArrayBuffer !== 'undefined'
        ? new Int32Array(new SharedArrayBuffer(4)) : null
      const fail = (error: string) => {
        if (worker.current !== next) return
        release()
        setRun(current => current && { ...current, state: 'Failed', error })
      }
      next.onerror = event => { event.preventDefault(); fail(event.message || 'Could not start the simulation worker.') }
      next.onmessageerror = () => fail('Could not read the simulation frame.')
      next.onmessage = ({ data }: MessageEvent<SimulationMessage>) => {
        if (worker.current !== next || currentKey.current !== key) return
        if (data.type === 'error') { fail(data.error); return }
        if (data.type === 'started') {
          setRun(current => current && { ...current, ...data, state: 'Running' })
        } else if (data.type === 'stored') {
          if (data.probeSamples.length !== recording.probes.length) { fail('The solver returned incompatible probe samples.'); return }
          data.probeSamples.forEach((samples, i) => recording.series[i].append(samples))
          setRun(current => current && { ...current, frameCount: data.index + 1,
            stepsDone: data.index * current.visualizeEvery + 1, performance: data.performance })
        } else if (data.type === 'completed') {
          release()
          setRun(current => current && { ...current, state: 'Completed' })
        }
      }
      const request: SimulationRequest = { projectJson: JSON.stringify(project), runtimeUrl: waveDudeUrl(), directory: recording.directory,
        cancellation: cancellation.current ?? undefined }
      next.postMessage(request)
    } catch (error) {
      if (operation.current !== version) return
      release()
      setRun(current => current && { ...current, state: 'Failed', error: error instanceof Error ? error.message : String(error) })
    }
  }

  useEffect(() => {
    if (snapshot.current && snapshot.current !== key) stop()
  }, [key])
  useEffect(() => {
    const version = ++readVersion.current
    pendingRead.current = null
    const recording = store.current
    if (!run?.frameCount || !recording?.directory) return
    const { directory, runId } = recording
    const index = position == null ? run.frameCount - 1 : Math.min(position, run.frameCount - 1)
    const { width, height } = run
    pendingRead.current = async () => {
      try {
        const selected = await readFrame(directory, runId, index, width, height, recording.probes, recording.series, run.visualizeEvery)
        if (readVersion.current === version && store.current === recording) setFrame(selected)
      } catch (error) {
        if (readVersion.current !== version || store.current !== recording) return
        release()
        setRun(current => current && { ...current, state: 'Failed', error: error instanceof Error ? error.message : String(error) })
      }
    }
    // Coalesce rapid seeks/progress updates: one read in flight and one latest request, no frame cache.
    if (!reading.current) {
      reading.current = true
      void (async () => {
        try {
          while (pendingRead.current) {
            const read = pendingRead.current
            pendingRead.current = null
            await read()
          }
        } finally {
          reading.current = false
        }
      })()
    }
  }, [run?.runId, run?.frameCount, position])
  useEffect(() => () => {
    release()
    void discard().catch(console.error)
  }, [])

  return {
    run: run && { ...run, isStale: snapshot.current !== key, probesStale: snapshot.current !== key }, busy,
    frame,
    position, seek, start, stop, clear,
  }
}
