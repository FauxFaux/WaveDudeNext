import type { ProjectSelection } from './ProjectTree'
import { SourceView } from '../Views/SourceView'
import type { SourceDraft } from '../Views/SourceView'

interface PropertiesPaneProps {
  selection: ProjectSelection
  source: SourceDraft
  onSourceChange: (source: SourceDraft) => void
}

const selectionNames: Record<ProjectSelection, string> = {
  scene: 'Scene', simulation: 'Simulation', display: 'Display', source: 'Source',
  parts: 'Parts', probes: 'Probes', colours: 'Colour schemes',
}

export function PropertiesPane({ selection, source, onSourceChange }: PropertiesPaneProps) {
  return (
    <section class="properties-pane" aria-labelledby="properties-heading">
      <header class="pane-header"><h2 id="properties-heading">Properties</h2><span>{selectionNames[selection]}</span></header>
      <div class="properties-content">
        {selection === 'source' ? <SourceView source={source} onChange={onSourceChange} /> : (
          <div class="properties-placeholder"><h3>{selectionNames[selection]}</h3>
            <p>{selection === 'parts' ? 'No optical parts in this project.' : selection === 'probes' ? 'No probes in this project.' : 'Properties will be available when connected to the server.'}</p>
          </div>
        )}
      </div>
    </section>
  )
}
