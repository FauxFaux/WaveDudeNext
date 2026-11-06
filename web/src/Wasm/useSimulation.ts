import { useEffect, useRef, useState } from 'preact/hooks'
import type { FramePair, RunStatus } from '../Api/Frames'
import type { Project } from '../Model/Project'
import { waveDudeUrl } from './Runtime'
import { createFrameStore, deleteFrameStore, readFrame } from './FrameStore'
import type { SimulationMessage, SimulationRequest } from './Simulation.worker'

interface Recording {
  runId: string
  directory?: FileSystemDirectoryHandle
}

/** Keep the selected picture in memory; all recorded frames are read from OPFS. */
export function useSimulation(project: Project) {
  const worker = useRef<Worker | null>(null)
  const operation = useRef(0)
  const store = useRef<Recording | null>(null)
  const snapshot = useRef<Project | null>(null)
  const currentProject = useRef(project)
  currentProject.current = project
  const [run, setRun] = useState<RunStatus | null>(null)
  const [frame, setFrame] = useState<FramePair>()
  const [position, seek] = useState<number | null>(null)
  const pendingRead = useRef<(() => Promise<void>) | null>(null)
  const reading = useRef(false)
  const readVersion = useRef(0)
  const busy = run?.state === 'Starting' || run?.state === 'Running'

  function release() {
    operation.current++
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
    snapshot.current = project
    setFrame(undefined)
    seek(null)
    const runId = crypto.randomUUID()
    const recording: Recording = { runId }
    store.current = recording
    setRun({
      runId, projectRevision: 1, state: 'Starting', stepsDone: 0, totalSteps: 0,
      frameCount: 0, totalFrames: 0, width: project.scene.width, height: project.scene.height,
      visualizeEvery: project.simulation.visualizeEvery, isStale: false, probesStale: true,
      error: null, hasIntensity: true, storageBudget: 0,
    })
    try {
      await previous
      if (operation.current !== version) return
      recording.directory = await createFrameStore(runId)
      if (operation.current !== version || currentProject.current !== project) {
        await deleteFrameStore(runId)
        return
      }
      const next = new Worker(new URL('./Simulation.worker.ts', import.meta.url), { type: 'module' })
      worker.current = next
      const fail = (error: string) => {
        if (worker.current !== next) return
        release()
        setRun(current => current && { ...current, state: 'Failed', error })
      }
      next.onerror = event => { event.preventDefault(); fail(event.message || 'Could not start the simulation worker.') }
      next.onmessageerror = () => fail('Could not read the simulation frame.')
      next.onmessage = ({ data }: MessageEvent<SimulationMessage>) => {
        if (worker.current !== next || currentProject.current !== project) return
        if (data.type === 'error') { fail(data.error); return }
        if (data.type === 'started') {
          setRun(current => current && { ...current, ...data, state: 'Running' })
        } else if (data.type === 'stored') {
          setRun(current => current && { ...current, frameCount: data.index + 1,
            stepsDone: data.index * current.visualizeEvery + 1 })
        } else if (data.type === 'completed') {
          release()
          setRun(current => current && { ...current, state: 'Completed' })
        }
      }
      const request: SimulationRequest = { projectJson: JSON.stringify(project), runtimeUrl: waveDudeUrl(), directory: recording.directory }
      next.postMessage(request)
    } catch (error) {
      if (operation.current !== version) return
      release()
      setRun(current => current && { ...current, state: 'Failed', error: error instanceof Error ? error.message : String(error) })
    }
  }

  useEffect(() => {
    if (snapshot.current && snapshot.current !== project) stop()
  }, [project])
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
        const selected = await readFrame(directory, runId, index, width, height)
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
    run: run && { ...run, isStale: snapshot.current !== project }, busy,
    frame,
    position, seek, start, stop, clear,
  }
}
