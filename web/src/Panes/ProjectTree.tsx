import { IconView } from '../Controls/IconView'
import type { IconName } from '../Controls/IconView'

export type ProjectSelection = 'scene' | 'simulation' | 'display' | 'source' | 'parts' | 'probes' | 'colours'

interface ProjectTreeProps {
  selection: ProjectSelection
  onSelect: (selection: ProjectSelection) => void
  sourceName: string
}

export function ProjectTree({ selection, onSelect, sourceName }: ProjectTreeProps) {
  function item(id: ProjectSelection, label: string, icon: IconName) {
    return (
      <button class={`project-item ${selection === id ? 'selected' : ''}`} aria-pressed={selection === id} onClick={() => onSelect(id)}>
        <IconView name={icon} /><span>{label}</span>
      </button>
    )
  }
  return (
    <section class="project-pane" aria-labelledby="project-heading">
      <header class="pane-header"><h2 id="project-heading">Project</h2></header>
      <div class="project-toolbar" aria-label="Add project objects">
        {([['source', 'Source'], ['probe', 'Probe'], ['part', 'Part']] as const).map(([icon, label]) => (
          <button key={icon} disabled title="Requires the simulation server"><IconView name={icon} />{label}<span class="toolbar-plus">+</span></button>
        ))}
      </div>
      <nav class="project-tree" aria-label="Project objects">
        <details open><summary>Project</summary><div class="tree-children">
          {item('scene', 'Scene', 'scene')}{item('simulation', 'Simulation', 'simulation')}{item('display', 'Display', 'display')}
          <details open class="source-group">
            <summary><IconView name="source" /><span>Sources</span><span class="object-count">1</span></summary>
            <div class="tree-children">{item('source', sourceName || 'Unnamed source', 'source')}</div>
          </details>
          {item('parts', 'Parts', 'part')}{item('probes', 'Probes', 'probe')}{item('colours', 'Colour schemes', 'colours')}
        </div></details>
      </nav>
    </section>
  )
}
