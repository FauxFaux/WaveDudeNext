import { useEffect, useRef, useState } from 'preact/hooks'
import { fetchFramePair } from './Frames'
import type { FramePair } from './Frames'
import type { RunStatus } from './Workspace'

// One pair in flight and only the newest pending position. Completed pairs replace both layers together.
export function useFrames(workspaceId: string | undefined, run: RunStatus | null | undefined, index: number, includeProbes = false) {
  const [frame, setFrame] = useState<FramePair | null>(null)
  const [error, setError] = useState<string | null>(null)
  const serial = useRef(0)
  const flight = useRef<{ controller: AbortController, done: Promise<void> } | null>(null)
  const runId = run?.runId
  const revision = run?.projectRevision
  const available = !!run && index >= 0 && index < run.frameCount

  useEffect(() => {
    const request = ++serial.current
    const previous = flight.current
    previous?.controller.abort()
    setError(null)
    if (!workspaceId || !run || !available) return
    const controller = new AbortController()
    const done = (async () => {
      await previous?.done
      if (controller.signal.aborted) return
      try {
        const next = await fetchFramePair(workspaceId, run, index, controller.signal, { includeProbes })
        if (!controller.signal.aborted && serial.current === request) setFrame(next)
      } catch (reason) {
        controller.abort() // Abort the other plane too if one request fails.
        if (serial.current === request && !(reason instanceof DOMException && reason.name === 'AbortError')) {
          setError(reason instanceof Error ? reason.message : 'Could not load frame.')
        }
      }
    })()
    flight.current = { controller, done }
    return () => { controller.abort(); serial.current++ }
  }, [workspaceId, runId, revision, available, index, includeProbes])

  // Never show a previous run's frames under a new run's identity.
  return { frame: frame && frame.runId === runId && frame.projectRevision === revision ? frame : null, error }
}
