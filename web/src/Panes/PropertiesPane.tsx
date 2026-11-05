import type { ProjectSection, ProjectSelection } from './ProjectTree'
import { SourceView } from '../Views/SourceView'
import type { SourceDraft } from '../Views/SourceDraft'

interface PropertiesPaneProps {
  selection: ProjectSelection
  source: SourceDraft | undefined
  onSourceChange: (source: SourceDraft) => void
}

const selectionNames: Record<ProjectSection, string> = {
  scene: 'Scene', simulation: 'Simulation', display: 'Display',
  parts: 'Parts', probes: 'Probes', colours: 'Colour schemes',
}

export function PropertiesPane({ selection, source, onSourceChange }: PropertiesPaneProps) {
  const selectionName = typeof selection === 'string' ? selectionNames[selection] : 'Source'
  return (
    <section class="properties-pane" aria-labelledby="properties-heading">
      <header class="pane-header"><h2 id="properties-heading">Properties</h2><span>{selectionName}</span></header>
      <div class="properties-content">
        {typeof selection !== 'string' && source ? <SourceView key={selection.index} source={source} onChange={onSourceChange} /> : (
          <div class="properties-placeholder"><h3>{selectionName}</h3>
            <p>{selection === 'parts' ? 'No optical parts in this project.' : selection === 'probes' ? 'No probes in this project.' : 'Properties will be available when connected to the server.'}</p>
          </div>
        )}
      </div>
    </section>
  )
}
