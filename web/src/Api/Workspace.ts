import { z } from 'zod'

const WorkspaceStatusSchema = z.strictObject({
  workspaceId: z.uuid(),
  createdAt: z.iso.datetime({ offset: true }),
  statusSequence: z.number().int().min(1).max(Number.MAX_SAFE_INTEGER),
})
export type WorkspaceStatus = z.infer<typeof WorkspaceStatusSchema>

export async function createWorkspace(signal: AbortSignal): Promise<WorkspaceStatus> {
  const response = await fetch('/api/workspaces', { method: 'POST', signal })
  if (!response.ok) throw new Error(`Workspace creation failed (HTTP ${response.status}).`)
  return readWorkspaceStatus(await response.json())
}

export function readWorkspaceStatus(value: unknown): WorkspaceStatus {
  return WorkspaceStatusSchema.parse(value)
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
