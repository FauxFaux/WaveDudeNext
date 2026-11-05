import { ColorSchemeSchema, OpticalPartSchema, ProbeSchema, ProjectSchema, SourceSchema } from '../Api/Project.generated'
import type { Project, OpticalPart } from '../Api/Project.generated'
import { initialValue } from './SchemaDefaults'
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

export function createProject(): Project {
  const project = ProjectSchema.parse(initialValue(ProjectSchema))
  project.seed = crypto.getRandomValues(new Uint32Array(1))[0] % 2147483646 + 1
  // Display's constructor supplies these contextual defaults in the shared model.
  project.display.field.colorScheme = 'Default field'
  project.display.field.blackLevel = -0.05
  project.display.intensity.colorScheme = 'Default intensity'
  return project
}

export function readProject(text: string): Project {
  const project = ProjectSchema.parse(JSON.parse(text))
  for (const collection of Object.keys(collectionNames) as ProjectCollection[]) {
    const ids = project[collection].map(item => item.id)
    if (new Set(ids).size !== ids.length) throw new Error(`Duplicate object IDs in ${collectionNames[collection].toLowerCase()}.`)
  }
  return project
}

export function writeProject(project: Project): string {
  return JSON.stringify(project, null, 2)
}

/** Mirror Project.AddSource/AddPart/AddProbe/AddColorScheme while the Wasm model API is pending. */
export function addProjectObject(project: Project, collection: ProjectCollection, id: string, kind?: PartKind): Project {
  const x = Math.floor(project.scene.width / 2)
  const y = Math.floor(project.scene.height / 2)
  const names = new Set(project[collection].map(item => item.name.toLowerCase()))
  const partNames: Partial<Record<PartKind, string>> = {
    Fiber: 'Fibre', BraggReflector: 'Bragg reflector', GrinLens: 'GRIN lens',
    XorGate: 'XOR gate', AndGate: 'AND gate', OrGate: 'OR gate', ZonePlate: 'Zone plate',
  }
  const stem = collection === 'sources' ? 'Source' : collection === 'probes' ? 'Probe'
    : collection === 'colorSchemes' ? 'Scheme' : partNames[kind!] ?? kind ?? 'Lens'
  let suffix = 1
  while (names.has(`${stem} ${suffix}`.toLowerCase())) suffix++
  const identity = { id, name: `${stem} ${suffix}` }
  let object: ProjectObject
  if (collection === 'sources') {
    object = SourceSchema.parse({ ...initialValue(SourceSchema) as object, ...identity, x, y })
  } else if (collection === 'probes') {
    object = ProbeSchema.parse({ ...initialValue(ProbeSchema) as object, ...identity, ax: x, ay: y, bx: x + 100, by: y, boxX: x, boxY: y })
  } else if (collection === 'colorSchemes') {
    object = ColorSchemeSchema.parse({ ...initialValue(ColorSchemeSchema) as object, ...identity,
      stops: [{ position: 0, colorText: '#000000' }, { position: 1, colorText: '#ffffff' }],
    })
  } else {
    const schema = OpticalPartSchema.options.find(schema => schema.shape.kind.value === (kind ?? 'Lens'))!
    object = OpticalPartSchema.parse({ ...initialValue(schema) as object, ...identity, x, y })
    if (object.kind === 'Fiber') object.nodes = [
      { x: -100, y: 0, inX: 0, inY: 0, outX: 67, outY: 0, smooth: true },
      { x: 100, y: 0, inX: -67, inY: 0, outX: 0, outY: 0, smooth: true },
    ]
  }
  return { ...project, [collection]: [...project[collection], object] }
}

export function selectedObject(project: Project, selection: ProjectSelection) {
  return typeof selection === 'string' ? undefined : project[selection.collection].find(item => item.id === selection.id)
}

/** Patch the current model without mutating Preact state. */
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

/** Stable object IDs let selection survive renames and reordering. */
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
