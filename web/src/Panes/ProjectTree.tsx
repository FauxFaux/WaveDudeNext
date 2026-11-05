import { useEffect, useRef } from 'preact/hooks'
import { IconView } from '../Controls/IconView'
import type { IconName } from '../Controls/IconView'
import type { Source } from '../Model/Source'

export type ProjectSection = 'scene' | 'simulation' | 'display' | 'parts' | 'probes' | 'colours'
// Sources can only be appended for now, so their list index is stable across edits and renames.
// This is local selection state, not a server object identifier.
export type ProjectSelection = ProjectSection | { kind: 'source', index: number }

interface ProjectTreeProps {
  selection: ProjectSelection
  onSelect: (selection: ProjectSelection) => void
  sources: Source[]
  onAddSource: () => void
}

export function ProjectTree({ selection, onSelect, sources, onAddSource }: ProjectTreeProps) {
  const tree = useRef<HTMLElement>(null)

  useEffect(() => {
    tree.current?.querySelectorAll('details').forEach(group => { group.open = true })
  }, [sources.length])

  useEffect(() => {
    tree.current?.querySelector('.project-item.selected')?.scrollIntoView({ block: 'nearest' })
  }, [selection])

  function item(id: ProjectSelection, label: string, icon: IconName, selected = selection === id) {
    return (
      <button key={typeof id === 'string' ? id : id.index} class={`project-item ${selected ? 'selected' : ''}`} aria-pressed={selected} onClick={() => onSelect(id)}>
        <IconView name={icon} /><span>{label}</span>
      </button>
    )
  }
  return (
    <section class="project-pane" aria-labelledby="project-heading">
      <header class="pane-header"><h2 id="project-heading">Project</h2></header>
      <div class="project-toolbar" aria-label="Add project objects">
        <button id="ToolAddSource" aria-label="Add source" title="Add source" onClick={onAddSource}><IconView name="source" />Source<span class="toolbar-plus">+</span></button>
        {([['probe', 'Probe'], ['part', 'Part']] as const).map(([icon, label]) => (
          <button key={icon} disabled title="Requires the simulation server"><IconView name={icon} />{label}<span class="toolbar-plus">+</span></button>
        ))}
      </div>
      <nav ref={tree} class="project-tree" aria-label="Project objects">
        <details open><summary>Project</summary><div class="tree-children">
          {item('scene', 'Scene', 'scene')}{item('simulation', 'Simulation', 'simulation')}{item('display', 'Display', 'display')}
          <details open class="source-group">
            <summary><IconView name="source" /><span>Sources</span><span class="object-count">{sources.length}</span></summary>
            <div class="tree-children">{sources.map((source, index) => (
              item({ kind: 'source', index }, source.name || 'Unnamed source', 'source', typeof selection !== 'string' && selection.index === index)
            ))}</div>
          </details>
          {item('parts', 'Parts', 'part')}{item('probes', 'Probes', 'probe')}{item('colours', 'Colour schemes', 'colours')}
        </div></details>
      </nav>
    </section>
  )
}
