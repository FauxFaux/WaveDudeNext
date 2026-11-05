import { useEffect, useRef, useState } from 'preact/hooks'
import type { JSX } from 'preact'
import { IconView } from '../Controls/IconView'
import type { IconName } from '../Controls/IconView'
import { OpticalPartSchema } from '../Api/Project.generated'
import { collectionNames } from '../Model/Project'
import type { ObjectAction, ObjectSelection, PartKind, Project, ProjectCollection, ProjectSelection } from '../Model/Project'
import { propertyLabel } from '../Views/ProjectProperties'

interface ProjectTreeProps {
  selection: ProjectSelection
  onSelect: (selection: ProjectSelection) => void
  project: Project | undefined
  canEdit: boolean
  onAdd: (collection: ProjectCollection, partKind?: PartKind) => void
  onAction: (selection: ObjectSelection, action: ObjectAction) => void
}

const collectionIcons: Record<ProjectCollection, IconName> = {
  sources: 'source', parts: 'part', probes: 'probe', colorSchemes: 'colours',
}
const partKinds = OpticalPartSchema.options.map(schema => schema.shape.kind.value)

function PartPicker({ disabled, compact = false, onAdd }: { disabled: boolean, compact?: boolean, onAdd: (kind: PartKind) => void }) {
  return <select class={compact ? 'part-picker compact' : 'part-picker'} value="" disabled={disabled}
    aria-label="Add part" title="Add optical part" onClick={event => event.stopPropagation()}
    onChange={event => { onAdd(event.currentTarget.value as PartKind); event.currentTarget.value = '' }}>
    <option value="" disabled>{compact ? '+' : 'Part +'}</option>
    {partKinds.map(kind => <option key={kind} value={kind}>{propertyLabel(kind)}</option>)}
  </select>
}

export function ProjectTree({ selection, onSelect, project, canEdit, onAdd, onAction }: ProjectTreeProps) {
  const tree = useRef<HTMLElement>(null)
  const [expanded, setExpanded] = useState<Record<string, boolean>>({ project: true, sources: true, parts: true, probes: true, colorSchemes: true })

  useEffect(() => {
    if (typeof selection !== 'string') setExpanded(current => ({ ...current, project: true, [selection.collection]: true }))
  }, [selection])

  useEffect(() => {
    const selected = tree.current?.querySelector<HTMLElement>('.project-select.selected')
    selected?.scrollIntoView({ block: 'nearest' })
    if (document.activeElement === document.body || tree.current?.contains(document.activeElement)) selected?.focus()
  }, [selection, expanded])

  function groupToggle(event: JSX.TargetedEvent<HTMLDetailsElement>, key: string) {
    const open = event.currentTarget.open
    setExpanded(current => current[key] === open ? current : { ...current, [key]: open })
  }

  function keyDown(event: JSX.TargetedKeyboardEvent<HTMLElement>) {
    const target = event.target as HTMLElement
    if (!target.matches('.project-select')) return
    if (typeof selection !== 'string' && canEdit) {
      const action = event.key === 'Delete' ? 'remove'
        : event.key === ' ' ? 'toggle'
        : (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'd' ? 'duplicate'
        : event.altKey && event.key === 'ArrowUp' ? 'moveUp'
        : event.altKey && event.key === 'ArrowDown' ? 'moveDown' : undefined
      if (action) { event.preventDefault(); onAction(selection, action); return }
    }
    if (event.altKey || event.ctrlKey || event.metaKey) return
    const rows = [...tree.current!.querySelectorAll<HTMLElement>('.project-select')].filter(row => row.getClientRects().length)
    const index = rows.indexOf(target)
    const next = event.key === 'ArrowDown' ? rows[index + 1] : event.key === 'ArrowUp' ? rows[index - 1]
      : event.key === 'Home' ? rows[0] : event.key === 'End' ? rows.at(-1) : undefined
    if (next) { event.preventDefault(); next.focus() }
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault()
      const group = target.parentElement as HTMLDetailsElement
      if (target.tagName === 'SUMMARY' && (event.key === 'ArrowRight' || group.open)) {
        setExpanded(current => ({ ...current, [group.dataset.group!]: event.key === 'ArrowRight' }))
      } else if (event.key === 'ArrowLeft') {
        target.parentElement?.closest('.tree-children')?.parentElement?.querySelector<HTMLElement>(':scope > summary')?.focus()
      }
    }
  }

  function item(id: ProjectSelection, label: string, icon: IconName) {
    const object = typeof id === 'string' ? undefined : project?.[id.collection].find(item => item.id === id.id)
    const selected = typeof id === 'string' ? selection === id
      : typeof selection !== 'string' && selection.id === id.id && selection.collection === id.collection
    const enabled = object && ('enabled' in object ? object.enabled : 'active' in object ? object.active : undefined)
    const index = object && typeof id !== 'string' ? project![id.collection].findIndex(item => item.id === id.id) : -1
    return <div key={typeof id === 'string' ? id : id.id} class={`project-row ${enabled === false ? 'object-disabled' : ''}`}>
      {typeof enabled === 'boolean' && <input type="checkbox" checked={enabled} disabled={!canEdit}
        aria-label={`${enabled ? 'Disable' : 'Enable'} ${label}`} onChange={() => typeof id !== 'string' && onAction(id, 'toggle')} />}
      <button class={`project-item project-select ${selected ? 'selected' : ''}`} aria-pressed={selected}
        onFocus={() => onSelect(id)} onClick={() => onSelect(id)}>
        {object && 'stops' in object ? <span class="scheme-swatch" style={{ background: `linear-gradient(to right, ${object.stops.map(stop => `${stop.colorText} ${stop.position * 100}%`).join(', ')})` }} /> : <IconView name={icon} />}
        <span>{label}</span>
      </button>
      {typeof id !== 'string' && <div class="object-actions">
        <button disabled={!canEdit || index <= 0} aria-label={`Move ${label} up`} title="Move up (Alt+Up)" onClick={() => onAction(id, 'moveUp')}>↑</button>
        <button disabled={!canEdit || index >= project![id.collection].length - 1} aria-label={`Move ${label} down`} title="Move down (Alt+Down)" onClick={() => onAction(id, 'moveDown')}>↓</button>
        <button disabled={!canEdit} aria-label={`Duplicate ${label}`} title="Duplicate (Ctrl+D)" onClick={() => onAction(id, 'duplicate')}>⧉</button>
        <button disabled={!canEdit} aria-label={`Remove ${label}`} title="Remove (Delete)" onClick={() => onAction(id, 'remove')}>−</button>
      </div>}
    </div>
  }

  return (
    <section class="project-pane" aria-labelledby="project-heading">
      <header class="pane-header"><h2 id="project-heading">Project</h2></header>
      <div class="project-toolbar" aria-label="Add project objects">
        <button id="ToolAddSource" disabled={!canEdit} aria-label="Add source" title="Add source" onClick={() => onAdd('sources')}><IconView name="source" />Source<span class="toolbar-plus">+</span></button>
        <button disabled={!canEdit} aria-label="Add probe" title="Add probe" onClick={() => onAdd('probes')}><IconView name="probe" />Probe<span class="toolbar-plus">+</span></button>
        <PartPicker disabled={!canEdit} onAdd={kind => onAdd('parts', kind)} />
      </div>
      <nav ref={tree} class="project-tree" aria-label="Project objects" onKeyDown={keyDown}>
        <details open={expanded.project} data-group="project" onToggle={event => groupToggle(event, 'project')}>
          <summary class={`project-select ${selection === 'project' ? 'selected' : ''}`} onFocus={() => onSelect('project')} onClick={() => onSelect('project')}>Project</summary>
          <div class="tree-children">
            {item('scene', 'Scene', 'scene')}{item('simulation', 'Simulation', 'simulation')}{item('display', 'Display', 'display')}
            {(Object.keys(collectionNames) as ProjectCollection[]).map(collection => (
              <details key={collection} open={expanded[collection]} data-group={collection} onToggle={event => groupToggle(event, collection)}>
                <summary class={`project-select ${selection === collection ? 'selected' : ''}`} onFocus={event => { if (event.target === event.currentTarget) onSelect(collection) }} onClick={() => onSelect(collection)}>
                  <IconView name={collectionIcons[collection]} /><span>{collectionNames[collection]}</span>
                  <span class="object-count">{project?.[collection].length ?? 0}</span>
                  {collection === 'parts' ? <PartPicker compact disabled={!canEdit} onAdd={kind => onAdd('parts', kind)} />
                    : <button disabled={!canEdit} class="collection-add" aria-label={`Add ${collection === 'sources' ? 'source' : collection === 'probes' ? 'probe' : 'colour scheme'}`}
                      onClick={event => { event.stopPropagation(); onAdd(collection) }}>+</button>}
                </summary>
                <div class="tree-children">
                  {project?.[collection].map(object => item({ collection, id: object.id }, object.name || 'Unnamed object', collectionIcons[collection]))}
                  {project && !project[collection].length && <p class="empty-collection">No {collectionNames[collection].toLowerCase()}.</p>}
                </div>
              </details>
            ))}
          </div>
        </details>
      </nav>
    </section>
  )
}
