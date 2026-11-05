import { z } from 'zod'

export const RunStatusSchema = z.strictObject({
  runId: z.uuid(),
  projectRevision: z.number().int().min(1),
  state: z.enum(['Starting', 'Running', 'Paused', 'Completed', 'Cancelled', 'Failed']),
  stepsDone: z.number().int().min(0),
  totalSteps: z.number().int().min(1),
  frameCount: z.number().int().min(0),
  totalFrames: z.number().int().min(1),
  width: z.number().int().min(1),
  height: z.number().int().min(1),
  visualizeEvery: z.number().int().min(1),
  isStale: z.boolean(),
  probesStale: z.boolean(),
  error: z.string().nullable(),
  hasIntensity: z.boolean(),
})
export type RunStatus = z.infer<typeof RunStatusSchema>

const WorkspaceStatusSchema = z.strictObject({
  workspaceId: z.uuid(),
  createdAt: z.iso.datetime({ offset: true }),
  statusSequence: z.number().int().min(1).max(Number.MAX_SAFE_INTEGER),
  projectRevision: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER),
  run: RunStatusSchema.nullable(),
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

export async function startSimulation(workspaceId: string, expectedRevision: number, signal: AbortSignal) {
  const response = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/runs`, {
    method: 'POST', signal, headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ expectedRevision }),
  })
  if (!response.ok) throw new Error(`Could not start simulation (HTTP ${response.status}).`)
  return RunStatusSchema.parse(await response.json())
}

export async function stopSimulation(workspaceId: string, runId: string, signal: AbortSignal) {
  const response = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/runs/${encodeURIComponent(runId)}/stop`, {
    method: 'POST', signal,
  })
  if (!response.ok) throw new Error(`Could not stop simulation (HTTP ${response.status}).`)
  return RunStatusSchema.parse(await response.json())
}
