import { useEffect, useRef, useState } from 'preact/hooks'
import { z } from 'zod'
import { readProject } from '../Model/Project'
import type { Project } from '../Model/Project'

interface ProjectJsonDialogProps {
  mode: 'open' | 'save'
  json: string
  onOpen: (project: Project) => void
  onClose: () => void
}

export function ProjectJsonDialog({ mode, json, onOpen, onClose }: ProjectJsonDialogProps) {
  const dialog = useRef<HTMLDialogElement>(null)
  const textarea = useRef<HTMLTextAreaElement>(null)
  const [text, setText] = useState(json)
  const [error, setError] = useState<string | null>(null)
  const opening = mode === 'open'

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    dialog.current?.showModal()
    textarea.current?.focus()
    if (!opening) textarea.current?.select()
    return () => { previous?.focus() }
  }, [])

  function close() {
    dialog.current?.close()
    onClose()
  }

  function open() {
    try {
      const project = readProject(text)
      onOpen(project)
      close()
    } catch (error) {
      setError(error instanceof z.ZodError
        ? error.issues.map(issue => `${issue.path.join('.') || 'Project'}: ${issue.message}`).join('\n')
        : error instanceof Error ? error.message : 'Could not read the project JSON.')
    }
  }

  return <dialog ref={dialog} class="project-json-dialog" aria-labelledby="project-json-title" onCancel={event => { event.preventDefault(); close() }}>
    <h2 id="project-json-title">{opening ? 'Open project' : 'Save project'}</h2>
    <p>{opening ? 'Paste project JSON to replace the current project.' : 'Copy this JSON to keep your project. Open it by pasting it into Open project.'}</p>
    <label for="project-json-text">Project JSON</label>
    <textarea ref={textarea} id="project-json-text" value={text} readOnly={!opening} spellcheck={false}
      aria-invalid={!!error} aria-describedby={error ? 'project-json-error' : undefined}
      onInput={event => { setText(event.currentTarget.value); setError(null) }} />
    {error && <p id="project-json-error" class="project-json-error" role="alert">{error}</p>}
    <div class="project-json-actions">
      {opening && <button onClick={open}>Open project</button>}
      {!opening && <button onClick={() => { textarea.current?.focus(); textarea.current?.select() }}>Select all</button>}
      <button onClick={close}>{opening ? 'Cancel' : 'Close'}</button>
    </div>
  </dialog>
}
