import { useEffect, useState } from 'preact/hooks'
import { createWorkspace, subscribeToWorkspace } from './Workspace'
import type { WorkspaceStatus } from './Workspace'

export function useWorkspace() {
  const [workspace, setWorkspace] = useState<WorkspaceStatus | null>(null)
  const [connection, setConnection] = useState('Creating workspace…')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    let unsubscribe: (() => void) | undefined

    async function connect() {
      try {
        const created = await createWorkspace(controller.signal)
        if (controller.signal.aborted) return
        setWorkspace(created)
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

  return { workspace, connection, error }
}
