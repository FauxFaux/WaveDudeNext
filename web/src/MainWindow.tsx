import { useEffect, useState } from 'preact/hooks'
import { IconView } from './Controls/IconView'
import { PreviewPane } from './Panes/PreviewPane'
import { ProjectTree } from './Panes/ProjectTree'
import type { ProjectSelection } from './Panes/ProjectTree'
import { PropertiesPane } from './Panes/PropertiesPane'
import { addSource, createProject } from './Model/Project'
import { applySourceDraft, createSourceDraft } from './Views/SourceDraft'
import type { SourceDraft } from './Views/SourceDraft'
import { useWorkspace } from './Api/useWorkspace'
import './MainWindow.css'

export function MainWindow() {
  const { workspace, connection, error } = useWorkspace()
  const [selection, setSelection] = useState<ProjectSelection>({ kind: 'source', index: 0 })
  const [project, setProject] = useState(() => addSource(createProject()))
  const [sourceDrafts, setSourceDrafts] = useState<Record<number, SourceDraft>>({})
  const [isModified, setIsModified] = useState(false)
  const [orientation, setOrientation] = useState<'stacked' | 'side-by-side'>('stacked')
  const selectedIndex = typeof selection !== 'string' ? selection.index : undefined
  const selectedSource = selectedIndex !== undefined ? project.sources[selectedIndex] : undefined
  const sourceDraft = selectedIndex !== undefined && selectedSource
    ? sourceDrafts[selectedIndex] ?? createSourceDraft(selectedSource) : undefined

  function addProjectSource() {
    const next = addSource(project)
    setProject(next)
    setSelection({ kind: 'source', index: next.sources.length - 1 })
    setIsModified(true)
  }

  function changeSource(draft: SourceDraft) {
    if (selectedIndex === undefined || !selectedSource) return
    setSourceDrafts(current => ({ ...current, [selectedIndex]: draft }))
    setProject(current => ({
      ...current,
      sources: current.sources.map((source, index) => index === selectedIndex ? applySourceDraft(source, draft) : source),
    }))
    setIsModified(true)
  }

  useEffect(() => {
    document.title = `WaveDude Next — Untitled${isModified ? '*' : ''}`
  }, [isModified])

  useEffect(() => {
    const closeMenus = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent && event.key !== 'Escape') return
      document.querySelectorAll<HTMLDetailsElement>('.main-menu details[open]').forEach(menu => {
        if (event instanceof MouseEvent && menu.contains(event.target as Node)) return
        menu.open = false
        if (event instanceof KeyboardEvent) menu.querySelector('summary')?.focus()
      })
    }
    document.addEventListener('click', closeMenus)
    document.addEventListener('keydown', closeMenus)
    return () => {
      document.removeEventListener('click', closeMenus)
      document.removeEventListener('keydown', closeMenus)
    }
  }, [])

  function newProject() {
    setProject(addSource(createProject()))
    setSourceDrafts({})
    setSelection({ kind: 'source', index: 0 })
    setIsModified(false)
    document.querySelectorAll<HTMLDetailsElement>('.main-menu details').forEach(menu => { menu.open = false })
  }

  return (
    <div class="main-window">
      <header class="menu-bar">
        <span class="app-mark" title="WaveDude Next"><IconView name="wave" /></span>
        <nav class="main-menu" aria-label="Main menu">
          <details><summary>File</summary><div class="menu-popup">
            <button onClick={newProject}>New project</button>
            <button disabled>Open project…</button><button disabled>Save project</button>
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
            <button disabled>Run <kbd>F5</kbd></button><button disabled>Pause</button><button disabled>Stop</button>
            <hr /><p>Simulation is not available yet</p>
          </div></details>
          <details><summary>Help</summary><div class="menu-popup about-popup">
            <strong>WaveDude Next</strong><p>2D wave simulator</p>
            <p>Browser interface preview. Project edits are local to this page; server simulation is not available yet.</p>
          </div></details>
        </nav>
        <span class="document-title">Untitled{isModified && <span class="modified-mark"> *</span>}</span>
        <span class="application-name">WaveDude Next</span>
      </header>
      <main class="workspace">
        <aside class="project-column" aria-label="Project and properties">
          <ProjectTree selection={selection} onSelect={setSelection} sources={project.sources} onAddSource={addProjectSource} />
          <PropertiesPane selection={selection} source={sourceDraft} onSourceChange={changeSource} />
        </aside>
        <PreviewPane orientation={orientation} onOrientationChange={setOrientation} />
      </main>
      <footer class="status-bar" role="status">
        <span><span class="status-dot" />{isModified ? 'Local changes · not saved' : 'Ready'}</span>
        <span class="status-connection" title={error ?? workspace?.workspaceId}>{connection}</span><span class="status-preview">Interface preview</span>
      </footer>
    </div>
  )
}
