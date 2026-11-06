import { useEffect, useState } from 'preact/hooks'
import { IconView } from './Controls/IconView'
import { PreviewPane } from './Panes/PreviewPane'
import { ProjectTree } from './Panes/ProjectTree'
import { actOnProjectObject, addProjectObject, createProject, editProjectProperty, selectedObject, readProject, writeProject } from './Model/Project'
import type { ObjectAction, ObjectSelection, PartKind, ProjectCollection, ProjectSelection, PropertyPath } from './Model/Project'
import { PropertiesPane } from './Panes/PropertiesPane'
import { applySourceDraft, createSourceDraft } from './Views/SourceDraft'
import type { SourceDraft } from './Views/SourceDraft'
import { ProjectJsonDialog } from './Views/ProjectJsonDialog'
import { useSimulation } from './Wasm/useSimulation'
import type { Project } from './Model/Project'
import examples from './assets/examples/index.json'
import './MainWindow.css'

const { exampleProjects } = await import('./example-projects');

export function MainWindow() {
  const [project, setProject] = useState(createProject)
  const simulation = useSimulation(project)
  const [selection, setSelection] = useState<ProjectSelection>('project')
  const [sourceDrafts, setSourceDrafts] = useState<Record<string, SourceDraft>>({})
  const [isModified, setIsModified] = useState(false)
  const [jsonDialog, setJsonDialog] = useState<'open' | 'save' | null>(null)
  const [projectGeneration, setProjectGeneration] = useState(0)
  const [orientation, setOrientation] = useState<'stacked' | 'side-by-side'>('stacked')
  const selectedSource = typeof selection !== 'string' && selection.collection === 'sources'
    ? project?.sources.find(source => source.id === selection.id) : undefined
  const sourceDraft = selectedSource
    ? sourceDrafts[selectedSource.id] ?? createSourceDraft(selectedSource) : undefined

  function addObject(collection: ProjectCollection, kind?: PartKind) {
    const id = crypto.randomUUID()
    setProject(current => addProjectObject(current, collection, id, kind))
    setSelection({ collection, id })
    setIsModified(true)
  }

  function objectAction(target: ObjectSelection, action: ObjectAction) {
    const copyId = action === 'duplicate' ? crypto.randomUUID() : undefined
    setProject(current => actOnProjectObject(current, target, action, copyId))
    if (copyId) setSelection({ ...target, id: copyId })
    if (action === 'remove') {
      setSelection(current => typeof current !== 'string' && current.id === target.id ? target.collection : current)
      setSourceDrafts(current => {
        const next = { ...current }
        delete next[target.id]
        return next
      })
    }
    if (target.collection === 'sources' && action === 'toggle') {
      const source = project.sources.find(source => source.id === target.id)
      setSourceDrafts(current => source && current[target.id]
        ? { ...current, [target.id]: { ...current[target.id], enabled: !source.enabled } } : current)
    }
    setIsModified(true)
  }

  async function changeProperty(path: PropertyPath, value: unknown) {
    setProject(current => editProjectProperty(current, selection, path, value))
    setIsModified(true)
  }

  async function changeScheme(layer: 'field' | 'intensity', name: string) {
    setProject(current => editProjectProperty(current, 'display', [layer, 'colorScheme'], name))
    setIsModified(true)
  }

  function changeSource(draft: SourceDraft) {
    if (!selectedSource) return
    const id = selectedSource.id
    setSourceDrafts(current => ({ ...current, [id]: draft }))
    setProject(current => ({ ...current, sources: current.sources.map(source => source.id === id ? applySourceDraft(source, draft) : source) }))
    setIsModified(true)
  }

  useEffect(() => {
    document.title = `WaveDude Next — Untitled${isModified ? '*' : ''}`
  }, [isModified])

  useEffect(() => {
    if (project && typeof selection !== 'string' && !selectedObject(project, selection)) setSelection(selection.collection)
  }, [project, selection])

  useEffect(() => {
    const closeMenus = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent && event.key !== 'Escape') return
      if (event instanceof KeyboardEvent) {
        const menu = document.activeElement?.closest<HTMLDetailsElement>('.main-menu details[open]')
        if (menu) {
          menu.open = false
          menu.querySelector('summary')?.focus()
          event.preventDefault()
        }
        return
      }
      document.querySelectorAll<HTMLDetailsElement>('.main-menu details[open]').forEach(menu => {
        if (menu.contains(event.target as Node)) return
        menu.open = false
      })
    }
    document.addEventListener('click', closeMenus)
    document.addEventListener('keydown', closeMenus)

    return () => {
      document.removeEventListener('click', closeMenus)
      document.removeEventListener('keydown', closeMenus)
    }
  }, [])

  function closeMenus() {
    document.querySelectorAll<HTMLDetailsElement>('.main-menu details').forEach(menu => { menu.open = false })
  }

  function replaceProject(next: Project) {
    simulation.clear()
    setProject(next)
    setSourceDrafts({})
    setSelection('project')
    setIsModified(false)
    setProjectGeneration(current => current + 1)
  }

  function openExample(id: string) {
    const path = `./assets/examples/${id.replace(/\.wdproj$/, '.json')}`
    replaceProject(readProject(exampleProjects[path]))
    closeMenus()
    document.querySelector<HTMLElement>('.main-menu > details > summary')?.focus()
  }

  function newProject() {
    replaceProject(createProject())
    closeMenus()
  }

  function showJsonDialog(mode: 'open' | 'save') {
    closeMenus()
    document.querySelector<HTMLElement>('.main-menu > details > summary')?.focus()
    setJsonDialog(mode)
  }

  return (
    <div class="main-window">
      <header class="menu-bar">
        <span class="app-mark" title="WaveDude Next"><IconView name="wave" /></span>
        <nav class="main-menu" aria-label="Main menu">
          <details><summary>File</summary><div class="menu-popup">
            <button onClick={newProject}>Reset project</button>
            <button onClick={() => showJsonDialog('open')}>Open project…</button>
            <details class="examples-menu"><summary>Examples</summary><div class="menu-popup examples-popup">
              {examples.map(example => <button key={example.id} onClick={() => openExample(example.id)}>
                {example.number}. {example.title}
              </button>)}
            </div></details>
            <button onClick={() => showJsonDialog('save')}>Save project…</button>
            <hr /><button disabled>Export…</button>
          </div></details>
          <details><summary>Edit</summary><div class="menu-popup">
            <button disabled>Undo</button><button disabled>Redo</button><hr /><button disabled>Delete</button>
          </div></details>
          <details><summary>View</summary><div class="menu-popup">
            <button aria-pressed={orientation === 'stacked'} onClick={() => setOrientation('stacked')}>Stacked previews</button>
            <button aria-pressed={orientation === 'side-by-side'} onClick={() => setOrientation('side-by-side')}>Side-by-side previews</button>
          </div></details>
          <details><summary>Simulation</summary><div class="menu-popup">
            <button disabled={simulation.busy} onClick={() => { simulation.start(); closeMenus() }}>Run</button><button disabled>Pause</button>
            <button disabled={!simulation.busy} onClick={() => { simulation.stop(); closeMenus() }}>Stop</button>
          </div></details>
          <details><summary>Help</summary><div class="menu-popup about-popup">
            <strong>WaveDude Next</strong><p>2D wave simulator</p>
            <p>Projects are edited in this page and cleared when it is reloaded. Use Open and Save to paste or copy project JSON. Run computes up to three preview frames.</p>
          </div></details>
        </nav>
        <span class="document-title">Untitled{isModified && <span class="modified-mark"> *</span>}</span>
        <span class="application-name">WaveDude Next</span>
      </header>
      <main class="project-layout">
        <aside class="project-column" aria-label="Project and properties">
          <ProjectTree selection={selection} onSelect={setSelection} project={project} canEdit onAdd={addObject} onAction={objectAction} />
          <PropertiesPane key={projectGeneration} selection={selection} project={project} source={sourceDraft} onSourceChange={changeSource} onPropertyChange={changeProperty} disabled={false} />
        </aside>
        <PreviewPane key={projectGeneration} project={project} canEdit onSchemeChange={changeScheme}
          run={simulation.run} frame={simulation.frame} position={simulation.position} onSeek={simulation.seek}
          onRun={simulation.busy ? undefined : simulation.start} onStop={simulation.busy ? simulation.stop : undefined}
          selection={selection} onSelectProbe={id => setSelection({ collection: 'probes', id })}
          orientation={orientation} onOrientationChange={setOrientation} />
      </main>
      <footer class="status-bar" role="status">
        <span><span class="status-dot" />{simulation.run?.error
          ?? (simulation.busy ? 'Computing preview…' : simulation.run?.isStale ? 'Project changed · run again to update the preview'
            : simulation.run?.state === 'Completed' ? `Preview complete · ${simulation.run.frameCount} frames (maximum 3)`
              : simulation.run?.state === 'Cancelled' ? 'Simulation stopped'
                : isModified ? 'Project changed · copy JSON to keep your changes' : 'Ready')}</span>
        <span class="status-local">Project held in this page</span><span class="status-preview">Up to 3 preview frames</span>
      </footer>
      {jsonDialog && <ProjectJsonDialog mode={jsonDialog} json={jsonDialog === 'save' ? writeProject(project) : ''}
        onOpen={replaceProject} onClose={() => setJsonDialog(null)} />}
    </div>
  )
}
