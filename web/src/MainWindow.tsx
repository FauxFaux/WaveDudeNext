import { useEffect, useState } from 'preact/hooks'
import { IconView } from './Controls/IconView'
import { performanceDetails } from './Controls/PerformanceDetails'
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

function readExample(id: string) {
  const path = `./assets/examples/${id.replace(/\.wdproj$/, '.json')}`
  return readProject(exampleProjects[path])
}

export function MainWindow() {
  const [linkedExample] = useState(() => {
    const match = /^#\/do-run\?example=([1-9]\d*)$/.exec(window.location.hash)
    return match ? examples.find(example => example.number === Number(match[1])) : undefined
  })
  const [project, setProject] = useState(() => linkedExample ? readExample(linkedExample.id) : createProject())
  const simulation = useSimulation(project)
  const performance = simulation.run?.performance
  const performanceTitle = performance && performanceDetails(performance)
  const [copyLinkError, setCopyLinkError] = useState<string | null>(null)
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
    if (linkedExample) void simulation.start()
  }, [])

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
    replaceProject(readExample(id))
    closeMenus()
    document.querySelector<HTMLElement>('.main-menu > details > summary')?.focus()
  }

  async function copyExampleLink(number: number) {
    const url = new URL(window.location.href)
    url.hash = `/do-run?example=${number}`
    setCopyLinkError(null)
    try {
      await navigator.clipboard.writeText(url.href)
    } catch {
      setCopyLinkError('Could not copy the example link. Check clipboard permissions and try again.')
    }
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
            <details class="examples-menu"><summary>Examples<span class="submenu-indicator" aria-hidden="true">&gt;</span></summary><div class="menu-popup examples-popup">
              {examples.map(example => <div class="example-row" key={example.id}>
                <button onClick={() => openExample(example.id)}>{example.number}. {example.title}</button>
                <button class="copy-example-link" title={`Copy link to example ${example.number}`} aria-label={`Copy link to example ${example.number}`}
                  onClick={() => void copyExampleLink(example.number)}><IconView name="copy" /></button>
              </div>)}
              {copyLinkError && <p role="alert">{copyLinkError}</p>}
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
            <p>Projects are edited in this page and cleared when it is reloaded. Use Open and Save to paste or copy project JSON. Run stores frames on disc, increasing the frame interval to fit estimated browser storage.</p>
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
            : simulation.run?.state === 'Completed' ? `Preview complete · ${simulation.run.frameCount} frames on disc`
              : simulation.run?.state === 'Cancelled' ? 'Simulation stopped'
                : isModified ? 'Project changed · copy JSON to keep your changes' : 'Ready')}</span>
        {performance && <details class="status-performance" onKeyDown={event => {
          if (event.key !== 'Escape') return
          event.currentTarget.open = false
          event.currentTarget.querySelector('summary')?.focus()
          event.preventDefault()
        }}>
          <summary title="Open simulation performance details">
            {performance.stepsPerSecond.toLocaleString(undefined, { maximumFractionDigits: 1 })} steps/s
            {' · '}{performance.parallelism.toFixed(2)}× parallel
          </summary>
          <pre tabIndex={0} aria-label="Simulation performance details">{performanceTitle}</pre>
        </details>}
        <span class="status-local">Project held in this page</span><span class="status-preview">{simulation.run?.storageBudget
          ? `Every ${simulation.run.visualizeEvery} steps · ${(simulation.run.storageBudget / 2 ** 30).toFixed(2)} GiB disc budget`
          : 'Frames stored on disc'}</span>
      </footer>
      {jsonDialog && <ProjectJsonDialog mode={jsonDialog} json={jsonDialog === 'save' ? writeProject(project) : ''}
        onOpen={replaceProject} onClose={() => setJsonDialog(null)} />}
    </div>
  )
}
