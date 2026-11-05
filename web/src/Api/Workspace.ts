export interface WorkspaceStatus {
  workspaceId: string
  createdAt: string
  statusSequence: number
}

export async function createWorkspace(signal: AbortSignal): Promise<WorkspaceStatus> {
  const response = await fetch('/api/workspaces', { method: 'POST', signal })
  if (!response.ok) throw new Error(`Workspace creation failed (HTTP ${response.status}).`)
  return readWorkspaceStatus(await response.json())
}

export function readWorkspaceStatus(value: unknown): WorkspaceStatus {
  if (typeof value !== 'object' || value === null
    || !('workspaceId' in value) || typeof value.workspaceId !== 'string'
    || !/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(value.workspaceId)
    || !('createdAt' in value) || typeof value.createdAt !== 'string'
    || !Number.isFinite(Date.parse(value.createdAt))
    || !('statusSequence' in value) || typeof value.statusSequence !== 'number'
    || !Number.isSafeInteger(value.statusSequence) || value.statusSequence < 1) {
    throw new Error('The server returned an invalid workspace status.')
  }
  return { workspaceId: value.workspaceId, createdAt: value.createdAt, statusSequence: value.statusSequence }
}

export function subscribeToWorkspace(
  workspaceId: string,
  onStatus: (status: WorkspaceStatus) => void,
  onConnection: (connected: boolean) => void,
  onError: (message: string) => void,
): () => void {
  const events = new EventSource(`/api/workspaces/${encodeURIComponent(workspaceId)}/events`)
  events.onopen = () => onConnection(true)
  events.onerror = () => onConnection(false)
  events.addEventListener('workspace-status', event => {
    try {
      const status = readWorkspaceStatus(JSON.parse((event as MessageEvent<string>).data))
      if (status.workspaceId !== workspaceId) throw new Error('The server returned a different workspace.')
      onStatus(status)
    } catch (error) {
      events.close()
      onError(error instanceof Error ? error.message : 'Could not read workspace status.')
    }
  })
  return () => events.close()
}
