import { useEffect, useRef, useState } from 'preact/hooks'
import type { FramePair, RunStatus } from '../Api/Frames'
import type { Project } from '../Model/Project'
import { waveDudeUrl } from './Runtime'
import type { SimulationMessage, SimulationRequest } from './Simulation.worker'

/** A short preview owns at most three frames and one worker, including while replacing a run. */
export function useSimulation(project: Project) {
  const worker = useRef<Worker | null>(null)
  const snapshot = useRef<Project | null>(null)
  const currentProject = useRef(project)
  currentProject.current = project
  const [run, setRun] = useState<RunStatus | null>(null)
  const [frames, setFrames] = useState<FramePair[]>([])
  const [position, seek] = useState<number | null>(null)
  const busy = run?.state === 'Starting' || run?.state === 'Running'

  function release() {
    worker.current?.terminate()
    worker.current = null
  }

  function stop() {
    release()
    setRun(current => current && (current.state === 'Starting' || current.state === 'Running')
      ? { ...current, state: 'Cancelled' } : current)
  }

  function clear() {
    release()
    snapshot.current = null
    setRun(null)
    setFrames([])
    seek(null)
  }

  function start() {
    release()
    snapshot.current = project
    setFrames([])
    seek(null)
    const runId = crypto.randomUUID()
    setRun({
      runId, projectRevision: 1, state: 'Starting', stepsDone: 0, totalSteps: 0,
      frameCount: 0, totalFrames: 0, width: project.scene.width, height: project.scene.height,
      visualizeEvery: project.simulation.visualizeEvery, isStale: false, probesStale: true,
      error: null, hasIntensity: true,
    })
    try {
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
        } else if (data.type === 'frame') {
          setFrames(current => [...current, data.frame])
          setRun(current => current && { ...current, frameCount: data.frame.index + 1,
            stepsDone: data.frame.index * current.visualizeEvery + 1 })
        } else if (data.type === 'completed') {
          release()
          setRun(current => current && { ...current, state: 'Completed' })
        }
      }
      const request: SimulationRequest = { projectJson: JSON.stringify(project), runtimeUrl: waveDudeUrl(), runId }
      next.postMessage(request)
    } catch (error) {
      release()
      setRun(current => current && { ...current, state: 'Failed', error: error instanceof Error ? error.message : String(error) })
    }
  }

  useEffect(() => {
    if (snapshot.current && snapshot.current !== project) stop()
  }, [project])
  useEffect(() => release, [])

  return {
    run: run && { ...run, isStale: snapshot.current !== project }, busy,
    frame: frames[position == null ? frames.length - 1 : Math.min(position, frames.length - 1)],
    position, seek, start, stop, clear,
  }
}
