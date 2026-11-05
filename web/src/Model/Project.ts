import type { Project, OpticalPart } from '../Api/Project.generated'
export type { Project } from '../Api/Project.generated'

export type ProjectCollection = 'sources' | 'parts' | 'probes' | 'colorSchemes'
export type ProjectSection = 'project' | 'scene' | 'simulation' | 'display' | ProjectCollection
export type ObjectSelection = { collection: ProjectCollection, id: string }
export type ProjectSelection = ProjectSection | ObjectSelection
export type ProjectObject = Project[ProjectCollection][number]
export type ObjectAction = 'toggle' | 'moveUp' | 'moveDown' | 'duplicate' | 'remove'
export type PartKind = OpticalPart['kind']
export type PropertyPath = (string | number)[]

export const collectionNames: Record<ProjectCollection, string> = {
  sources: 'Sources', parts: 'Parts', probes: 'Probes', colorSchemes: 'Colour schemes',
}

export function selectedObject(project: Project, selection: ProjectSelection) {
  return typeof selection === 'string' ? undefined : project[selection.collection].find(item => item.id === selection.id)
}

/** Patch the latest acknowledged model so queued edits never replace unrelated changes. */
export function editProjectProperty(project: Project, selection: ProjectSelection, path: PropertyPath, value: unknown): Project {
  const next = structuredClone(project)
  const target = typeof selection === 'string'
    ? selection === 'project' ? next : next[selection]
    : selectedObject(next, selection)
  if (!target || !path.length) throw new Error('The selected object is no longer in the project.')
  let parent = target as unknown as Record<string | number, unknown>
  for (const key of path.slice(0, -1)) parent = parent[key] as typeof parent
  parent[path.at(-1)!] = value
  return next
}

/** Collection actions use workspace IDs; selection survives renames and reordering. */
export function actOnProjectObject(project: Project, selection: ObjectSelection, action: ObjectAction, copyId?: string): Project {
  const items: ProjectObject[] = [...project[selection.collection]]
  const index = items.findIndex(item => item.id === selection.id)
  if (index < 0) throw new Error('The selected object is no longer in the project.')
  const item = items[index]
  if (action === 'remove') items.splice(index, 1)
  if (action === 'toggle') {
    items[index] = 'enabled' in item ? { ...item, enabled: !item.enabled }
      : 'active' in item ? { ...item, active: !item.active } : item
  }
  if (action === 'moveUp' || action === 'moveDown') {
    const destination = index + (action === 'moveUp' ? -1 : 1)
    if (destination >= 0 && destination < items.length) {
      [items[index], items[destination]] = [items[destination], items[index]]
    }
  }
  if (action === 'duplicate') {
    if (!copyId) throw new Error('A duplicate needs a new object ID.')
    const names = new Set(items.map(item => item.name.toLowerCase()))
    const base = `${item.name} copy`
    let name = base
    for (let suffix = 2; names.has(name.toLowerCase()); suffix++) name = `${base} ${suffix}`
    items.splice(index + 1, 0, { ...structuredClone(item), id: copyId, name })
  }
  return { ...project, [selection.collection]: items }
}
