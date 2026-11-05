import { z } from 'zod'
import { ProjectSchema } from './Project.generated'
import type { Project } from './Project.generated'
import type { ProjectCollection, PartKind } from '../Model/Project'

export const ProjectSnapshotSchema = z.strictObject({
  revision: z.number().int().min(1).max(Number.MAX_SAFE_INTEGER),
  project: ProjectSchema,
})
export type ProjectSnapshot = z.infer<typeof ProjectSnapshotSchema>

async function requestProject(workspaceId: string, init: RequestInit, suffix = ''): Promise<ProjectSnapshot> {
  const response = await fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/project${suffix}`, {
    ...init,
    headers: { 'Content-Type': 'application/json' },
  })
  if (!response.ok) {
    throw new Error(response.status === 409
      ? 'The project changed on the server. Your draft is still available; reload before retrying.'
      : `Project request failed (HTTP ${response.status}). Your draft is still available.`)
  }
  return ProjectSnapshotSchema.parse(await response.json())
}

export function getProject(workspaceId: string, signal: AbortSignal) {
  return requestProject(workspaceId, { method: 'GET', signal })
}

export function postProject(workspaceId: string, expectedRevision: number, project: Project, signal: AbortSignal) {
  return requestProject(workspaceId, {
    method: 'POST', signal, body: JSON.stringify({ expectedRevision, project: ProjectSchema.parse(project) }),
  })
}

export function addProjectObject(workspaceId: string, expectedRevision: number, collection: ProjectCollection, partKind: PartKind | undefined, signal: AbortSignal) {
  const kinds = { sources: 'source', parts: 'part', probes: 'probe', colorSchemes: 'colorScheme' } as const
  return requestProject(workspaceId, { method: 'POST', signal, body: JSON.stringify({ expectedRevision, kind: kinds[collection], partKind }) }, '/objects')
}

export function openProjectExample(workspaceId: string, expectedRevision: number, exampleId: string, signal: AbortSignal) {
  return requestProject(workspaceId, {
    method: 'POST', signal, body: JSON.stringify({ expectedRevision, exampleId }),
  }, '/example')
}
