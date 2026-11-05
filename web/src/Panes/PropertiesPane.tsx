import { z } from 'zod'
import { ColorSchemeSchema, DisplaySchema, OpticalPartSchema, ProbeSchema, ProjectSchema, SceneSchema, SimulationSchema, SourceSchema } from '../Api/Project.generated'
import { collectionNames, selectedObject } from '../Model/Project'
import type { Project, ProjectSelection, PropertyPath } from '../Model/Project'
import { SourceView } from '../Views/SourceView'
import type { SourceDraft } from '../Views/SourceDraft'
import { ProjectProperties, propertyLabel } from '../Views/ProjectProperties'

interface PropertiesPaneProps {
  selection: ProjectSelection
  project: Project | undefined
  source: SourceDraft | undefined
  onSourceChange: (source: SourceDraft) => void
  onPropertyChange: (path: PropertyPath, value: unknown) => Promise<void>
  disabled: boolean
  openingProject?: boolean
}

const sectionSchemas = { project: ProjectSchema.pick({ seed: true, description: true }), scene: SceneSchema, simulation: SimulationSchema, display: DisplaySchema }

export function PropertiesPane({ selection, project, source, onSourceChange, onPropertyChange, disabled, openingProject }: PropertiesPaneProps) {
  const object = project && selectedObject(project, selection)
  const selectionName = typeof selection === 'string' ? propertyLabel(selection)
    : selection.collection === 'parts' && object && 'kind' in object ? propertyLabel(object.kind)
    : selection.collection === 'colorSchemes' ? 'Colour scheme' : selection.collection === 'probes' ? 'Probe' : 'Source'
  let schema: z.ZodObject | undefined
  let value: Record<string, unknown> | undefined
  if (project && typeof selection === 'string' && selection in sectionSchemas) {
    schema = sectionSchemas[selection as keyof typeof sectionSchemas]
    value = selection === 'project' ? project : project[selection as 'scene' | 'simulation' | 'display']
  } else if (object && typeof selection !== 'string') {
    schema = selection.collection === 'parts' ? OpticalPartSchema.options.find(schema => schema.shape.kind.value === ('kind' in object ? object.kind : undefined))
      : selection.collection === 'probes' ? ProbeSchema : ColorSchemeSchema
    value = object
  }
  const key = typeof selection === 'string' ? selection : selection.id
  return (
    <section class="properties-pane" aria-labelledby="properties-heading">
      <header class="pane-header"><h2 id="properties-heading">Properties</h2><span>{selectionName}</span></header>
      <div class="properties-content" inert={openingProject}>
        {typeof selection !== 'string' && selection.collection === 'sources' && source ? <div key={key}>
          <SourceView source={source} onChange={onSourceChange} />
          <ProjectProperties schema={SourceSchema.pick({ array: true })} value={{ array: object && 'array' in object ? object.array : null }} onChange={onPropertyChange} disabled={disabled} />
        </div> : schema && value ? <ProjectProperties key={key} schema={schema} value={value} onChange={onPropertyChange} disabled={disabled} /> : (
          <div class="properties-placeholder"><h3>{selectionName}</h3>
            <p>{!project ? 'Loading project…' : typeof selection === 'string' && selection in collectionNames
              ? `${project[selection as keyof typeof collectionNames].length} ${selectionName.toLowerCase()}. Expand the collection and select an item to edit its properties.`
              : 'Select an object to edit its properties.'}</p>
          </div>
        )}
      </div>
    </section>
  )
}
