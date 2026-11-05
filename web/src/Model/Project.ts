import { createScene } from './Scene'
import type { Scene } from './Scene'
import { createSource } from './Source'
import type { Source } from './Source'

// The portion of Model/Project.cs used by the browser so far, not a workspace API DTO.
// Simulation, display, parts, probes and colour schemes will be added with their flows.
export interface Project {
  version: number
  seed: number
  description: string
  scene: Scene
  sources: Source[]
}

export function createProject(): Project {
  return {
    version: 3,
    seed: crypto.getRandomValues(new Uint32Array(1))[0] % 2147483646 + 1,
    description: '', scene: createScene(), sources: [],
  }
}

/** Matches Project.AddSource: first free name (case-insensitive), at the scene centre. */
export function addSource(project: Project): Project {
  const names = new Set(project.sources.map(source => source.name.toLowerCase()))
  let number = 1
  while (names.has(`source ${number}`)) number++
  const source = createSource(`Source ${number}`, Math.trunc(project.scene.width / 2), Math.trunc(project.scene.height / 2))
  return { ...project, sources: [...project.sources, source] }
}
