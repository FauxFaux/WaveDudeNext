import { useEffect, useState } from 'preact/hooks'
import { getExamples } from '../Api/Examples'
import type { ExampleEntry } from '../Api/Examples'

interface ExampleMenuProps {
  canOpen: boolean
  onOpen: (example: ExampleEntry) => void
}

export function ExampleMenu({ canOpen, onOpen }: ExampleMenuProps) {
  const [examples, setExamples] = useState<ExampleEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setError(null)
    void getExamples(controller.signal).then(entries => {
      if (!controller.signal.aborted) setExamples(entries)
    }).catch(reason => {
      if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Could not list examples.')
    })
    return () => controller.abort()
  }, [attempt])

  return (
    <details class="example-menu">
      <summary>Open example<span aria-hidden="true">▸</span></summary>
      <div class="menu-popup example-list" aria-label="Examples">
        {error ? <><p role="alert">{error}</p><button onClick={() => setAttempt(value => value + 1)}>Retry</button></>
          : examples === null ? <p>Loading examples…</p>
          : examples.length === 0 ? <p>No examples available.</p>
          : examples.map(example => (
            <button key={example.id} disabled={!canOpen} onClick={() => onOpen(example)}>{example.number}. {example.title}</button>
          ))}
      </div>
    </details>
  )
}
