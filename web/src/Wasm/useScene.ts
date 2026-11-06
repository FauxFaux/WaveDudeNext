import { useEffect, useRef, useState } from 'preact/hooks'
import type { Project } from '../Model/Project'
import { sceneKey } from './Scene'
import type { ScenePreview } from './Scene'
import type { SceneMessage, SceneRequest } from './Scene.worker'
import { waveDudeUrl } from './Runtime'

interface SceneResult {
  key: string
  scene: ScenePreview | null
  error: string | null
}

// Scene images follow current geometry independently of retained, possibly outdated wave frames.
export function useScene(project: Project | undefined) {
  const key = sceneKey(project)
  const currentKey = useRef(key)
  currentKey.current = key
  const [result, setResult] = useState<SceneResult | null>(null)

  useEffect(() => {
    if (!key || result?.key === key) return
    let active = true
    let worker: Worker | undefined
    const finish = (scene: ScenePreview | null, error: string | null) => {
      if (!active || currentKey.current !== key) return
      setResult({ key, scene, error })
      worker?.terminate()
    }
    try {
      worker = new Worker(new URL('./Scene.worker.ts', import.meta.url), { type: 'module' })
      worker.onerror = event => { event.preventDefault(); finish(null, event.message || 'Could not start the scene worker.') }
      worker.onmessageerror = () => finish(null, 'Could not read the scene images.')
      worker.onmessage = ({ data }: MessageEvent<SceneMessage>) => {
        if (data.type === 'error') { finish(null, data.error); return }
        try {
          const pixels = data.scene
          finish(pixels ? { width: pixels.width, height: pixels.height,
            image: new ImageData(pixels.image, pixels.width, pixels.height),
            edges: new ImageData(pixels.edges, pixels.width, pixels.height) } : null, null)
        } catch (error) {
          finish(null, error instanceof Error ? error.message : 'Could not load scene images.')
        }
      }
      const request: SceneRequest = { projectJson: key, runtimeUrl: waveDudeUrl() }
      worker.postMessage(request)
    } catch (error) {
      finish(null, error instanceof Error ? error.message : 'Could not start the scene worker.')
    }
    return () => { active = false; worker?.terminate() }
  }, [key])

  // Hide old geometry immediately; terminated or late workers cannot restore it after an edit.
  return { scene: result?.key === key ? result.scene : null, error: result?.key === key ? result.error : null }
}
