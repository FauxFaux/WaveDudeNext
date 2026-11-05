import { useEffect, useState } from 'preact/hooks'
import { IconView } from './Controls/IconView'
import { ExampleMenu } from './Controls/ExampleMenu'
import type { ExampleEntry } from './Api/Examples'
import { PreviewPane } from './Panes/PreviewPane'
import { ProjectTree } from './Panes/ProjectTree'
import { selectedObject } from './Model/Project'
import type { ObjectAction, ObjectSelection, PartKind, ProjectCollection, ProjectSelection, PropertyPath } from './Model/Project'
import { PropertiesPane } from './Panes/PropertiesPane'
import { acknowledgeSourceDraft, createSourceDraft } from './Views/SourceDraft'
import type { SourceDraft } from './Views/SourceDraft'
import { useWorkspace } from './Api/useWorkspace'
import './MainWindow.css'

export function MainWindow() {
  const { workspace, connection, error, project: snapshot, pending, editSource, addObject, editProperty, actOnObject, openExample, newProject: resetProject, startRun, stopRun } = useWorkspace()
  const run = workspace?.run
  const running = !!run && ['Starting', 'Running', 'Paused'].includes(run.state)
  const canRun = !!snapshot && pending === 0 && !error && !running
  const runSimulation = () => { void startRun().catch(() => {}) }
  const stopSimulation = () => { void stopRun().catch(() => {}) }
  const project = snapshot?.project
  const [selection, setSelection] = useState<ProjectSelection>('project')
  const [sourceDrafts, setSourceDrafts] = useState<Record<string, SourceDraft>>({})
  const [isModified, setIsModified] = useState(false)
  const [projectName, setProjectName] = useState('Untitled')
  const [openingExample, setOpeningExample] = useState(false)
  const [orientation, setOrientation] = useState<'stacked' | 'side-by-side'>('stacked')
  const selectedSource = typeof selection !== 'string' && selection.collection === 'sources'
    ? project?.sources.find(source => source.id === selection.id) : undefined
  const sourceDraft = selectedSource
    ? sourceDrafts[selectedSource.id] ?? createSourceDraft(selectedSource) : undefined

  function addProjectObject(collection: ProjectCollection, kind?: PartKind) {
    void addObject(collection, kind).then(snapshot => {
      const added = snapshot.project[collection].at(-1)
      if (added) setSelection({ collection, id: added.id })
      setIsModified(true)
    }).catch(() => {})
  }

  function objectAction(target: ObjectSelection, action: ObjectAction) {
    const copyId = action === 'duplicate' ? crypto.randomUUID() : undefined
    void actOnObject(target, action, copyId).then(snapshot => {
      if (action === 'duplicate' && copyId) setSelection({ ...target, id: copyId })
      if (action === 'remove') setSelection(current => typeof current !== 'string' && current.id === target.id ? target.collection : current)
      if (target.collection === 'sources' && action === 'toggle') {
        setSourceDrafts(current => {
          const source = snapshot.project.sources.find(source => source.id === target.id)
          const draft = current[target.id]
          return source && draft ? { ...current, [target.id]: { ...draft, enabled: source.enabled } } : current
        })
      }
      setIsModified(true)
    }).catch(() => {})
  }

  async function changeProperty(path: PropertyPath, value: unknown) {
    await editProperty(selection, path, value)
    setIsModified(true)
  }

  async function changeScheme(layer: 'field' | 'intensity', name: string) {
    await editProperty('display', [layer, 'colorScheme'], name)
    setIsModified(true)
  }

  function changeSource(draft: SourceDraft) {
    if (!selectedSource) return
    setSourceDrafts(current => ({ ...current, [selectedSource.id]: draft }))
    const id = selectedSource.id
    void editSource(id, draft).then(snapshot => {
      const source = snapshot?.project.sources.find(source => source.id === id)
      if (!source) return
      setSourceDrafts(current => current[id] === draft
        ? { ...current, [id]: acknowledgeSourceDraft(source, draft) } : current)
    }).catch(() => {})
    setIsModified(true)
  }

  useEffect(() => {
    document.title = `WaveDude Next — ${projectName}${isModified ? '*' : ''}`
  }, [projectName, isModified])

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

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'F5') return
      event.preventDefault()
      if (canRun && !openingExample) runSimulation()
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [canRun, openingExample, startRun])

  function newProject() {
    resetProject()
    setSourceDrafts({})
    setSelection('project')
    setIsModified(false)
    setProjectName('Untitled')
    document.querySelectorAll<HTMLDetailsElement>('.main-menu details').forEach(menu => { menu.open = false })
  }

  async function openProjectExample(example: ExampleEntry) {
    setOpeningExample(true)
    document.querySelectorAll<HTMLDetailsElement>('.main-menu details').forEach(menu => { menu.open = false })
    document.querySelector<HTMLElement>('.main-menu > details > summary')?.focus()
    try {
      const next = await openExample(example.id)
      setSourceDrafts({})
      setSelection(next.project.sources.length ? { collection: 'sources', id: next.project.sources[0].id } : 'scene')
      setProjectName(example.title)
      setIsModified(false)
    } catch {
      // The workspace reports the error and retains the previous project and drafts.
    } finally {
      setOpeningExample(false)
    }
  }

  return (
    <div class="main-window">
      <header class="menu-bar">
        <span class="app-mark" title="WaveDude Next"><IconView name="wave" /></span>
        <nav class="main-menu" aria-label="Main menu">
          <details><summary>File</summary><div class="menu-popup">
            <button onClick={newProject}>New project</button>
            <button disabled>Open project…</button>
            <ExampleMenu canOpen={!!project && pending === 0 && !openingExample && !error} onOpen={example => { void openProjectExample(example) }} />
            <button disabled>Save project</button>
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
            <button disabled={!canRun || openingExample} onClick={runSimulation}>Run <kbd>F5</kbd></button><button disabled>Pause</button><button disabled={!running || pending > 0} onClick={stopSimulation}>Stop</button>
          </div></details>
          <details><summary>Help</summary><div class="menu-popup about-popup">
            <strong>WaveDude Next</strong><p>2D wave simulator</p>
            <p>Browser interface preview. Projects and simulation frames are held on the server until shutdown. Stored frames can be viewed and scrubbed in the timeline.</p>
          </div></details>
        </nav>
        <span class="document-title">{projectName}{isModified && <span class="modified-mark"> *</span>}</span>
        <span class="application-name">WaveDude Next</span>
      </header>
      <main class="workspace">
        <aside class="project-column" aria-label="Project and properties">
          <ProjectTree selection={selection} onSelect={setSelection} project={project} canEdit={!!project && pending === 0 && !openingExample && !error} onAdd={addProjectObject} onAction={objectAction} />
          <PropertiesPane selection={selection} project={project} source={sourceDraft} onSourceChange={changeSource} onPropertyChange={changeProperty} disabled={!project || pending > 0 || openingExample || !!error} openingProject={openingExample} />
        </aside>
        <PreviewPane workspaceId={workspace?.workspaceId} project={project} canEdit={!!project && pending === 0 && !openingExample && !error} onSchemeChange={changeScheme}
          selection={selection} onSelectProbe={id => setSelection({ collection: 'probes', id })}
          orientation={orientation} onOrientationChange={setOrientation} canRun={canRun && !openingExample} canStop={running && pending === 0} onRun={runSimulation} onStop={stopSimulation} run={run} />
      </main>
      <footer class="status-bar" role="status">
        <span><span class="status-dot" />{error ? error : openingExample ? 'Opening example…' : pending ? 'Sending project changes…' : run?.error ? run.error : run ? `${run.isStale ? 'Outdated simulation' : run.state} · ${run.frameCount} / ${run.totalFrames} frames buffered` : isModified ? 'Changes held on server · not saved to file' : project ? 'Ready' : 'Loading project…'}</span>
        <span class="status-connection" title={error ?? workspace?.workspaceId}>{connection}</span><span class="status-preview">Interface preview</span>
      </footer>
    </div>
  )
}
