import { useEffect, useRef, useState } from 'preact/hooks'
import { createWorkspace, subscribeToWorkspace, startSimulation, stopSimulation } from './Workspace'
import type { WorkspaceStatus } from './Workspace'
import { addProjectObject, getProject, openProjectExample, postProject } from './Project'
import type { ProjectSnapshot } from './Project'
import type { SourceDraft } from '../Views/SourceDraft'
import { applySourceDraft } from '../Views/SourceDraft'
import { createMutationQueue } from './MutationQueue'
import { actOnProjectObject, editProjectProperty } from '../Model/Project'
import type { ObjectAction, ObjectSelection, PartKind, ProjectCollection, ProjectSelection, PropertyPath } from '../Model/Project'

export function useWorkspace() {
  const [workspace, setWorkspace] = useState<WorkspaceStatus | null>(null)
  const [connection, setConnection] = useState('Creating workspace…')
  const [project, setProject] = useState<ProjectSnapshot | null>(null)
  const queue = useRef<ReturnType<typeof createMutationQueue<ProjectSnapshot>> | null>(null)
  const workspaceId = useRef<string | null>(null)
  const cancellation = useRef(new AbortController())
  const [pending, setPending] = useState(0)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    cancellation.current = controller
    let unsubscribe: (() => void) | undefined

    async function connect() {
      try {
        const created = await createWorkspace(controller.signal)
        if (controller.signal.aborted) return
        setWorkspace(created)
        workspaceId.current = created.workspaceId
        const initial = await getProject(created.workspaceId, controller.signal)
        if (controller.signal.aborted) return
        queue.current = createMutationQueue(initial)
        setProject(initial)
        // Each page load gets an independent workspace, even if a tab copied session storage.
        try {
          sessionStorage.setItem('wavedude.workspaceId', created.workspaceId)
        } catch {
          // A blocked storage area must not prevent this page from using its workspace.
        }
        setConnection('Connecting to workspace…')
        unsubscribe = subscribeToWorkspace(created.workspaceId, status => {
          if (controller.signal.aborted) return
          setWorkspace(current => current?.workspaceId === status.workspaceId
            && status.statusSequence > current.statusSequence ? status : current)
        }, connected => {
          if (!controller.signal.aborted) {
            setConnection(connected ? 'Workspace connected' : 'Reconnecting to workspace…')
          }
        }, message => {
          if (!controller.signal.aborted) {
            setError(message)
            setConnection('Workspace connection failed')
          }
        })
      } catch (error) {
        if (controller.signal.aborted) return
        setError(error instanceof Error ? error.message : 'Could not create a workspace.')
        setConnection('Workspace connection failed')
      }
    }

    void connect()
    return () => {
      controller.abort()
      unsubscribe?.()
    }
  }, [])

  function enqueue(operation: (id: string, current: ProjectSnapshot, signal: AbortSignal) => Promise<ProjectSnapshot>) {
    setPending(count => count + 1)
    const id = workspaceId.current
    const task = queue.current && id
      ? queue.current.enqueue(current => operation(id, current, cancellation.current.signal))
      : Promise.reject(new Error('The project is not connected.'))
    const update = task.then(next => {
      if (!cancellation.current.signal.aborted) setProject(next)
      return next
    }).catch(reason => {
      if (!cancellation.current.signal.aborted) setError(reason instanceof Error ? reason.message : 'Project update failed.')
      throw reason
    }).finally(() => {
      if (!cancellation.current.signal.aborted) setPending(count => count - 1)
    })
    return update
  }

  function editSource(id: string, draft: SourceDraft) {
    return enqueue((workspace, current, signal) => {
      const source = current.project.sources.find(source => source.id === id)
      if (!source) throw new Error('The source is no longer in the project. Your draft is still available.')
      const next = applySourceDraft(source, draft)
      if (JSON.stringify(next) === JSON.stringify(source)) return Promise.resolve(current)
      return postProject(workspace, current.revision, {
        ...current.project, sources: current.project.sources.map(source => source.id === id ? next : source),
      }, signal)
    })
  }

  function addObject(collection: ProjectCollection, partKind?: PartKind) {
    return enqueue((workspace, current, signal) => addProjectObject(workspace, current.revision, collection, partKind, signal))
  }

  function editProperty(selection: ProjectSelection, path: PropertyPath, value: unknown) {
    return enqueue((workspace, current, signal) => postProject(workspace, current.revision,
      editProjectProperty(current.project, selection, path, value), signal))
  }

  function actOnObject(selection: ObjectSelection, action: ObjectAction, copyId?: string) {
    return enqueue((workspace, current, signal) => postProject(workspace, current.revision,
      actOnProjectObject(current.project, selection, action, copyId), signal))
  }

  function openExample(exampleId: string) {
    return enqueue((workspace, current, signal) => openProjectExample(workspace, current.revision, exampleId, signal))
  }

  function startRun() {
    return enqueue(async (workspace, current, signal) => {
      await startSimulation(workspace, current.revision, signal)
      // The revisioned SSE stream owns run status; an HTTP response can arrive after a newer heartbeat.
      return current
    })
  }

  function stopRun() {
    const runId = workspace?.run?.runId
    if (!runId) return Promise.resolve()
    return enqueue(async (workspace, current, signal) => {
      await stopSimulation(workspace, runId, signal)
      return current
    })
  }

  function newProject() {
    // Opening a fresh workspace also gives the new project its backend seed/defaults.
    window.location.reload()
  }

  return { workspace, connection, error, project, pending, editSource, addObject, editProperty, actOnObject, openExample, newProject, startRun, stopRun }
}
