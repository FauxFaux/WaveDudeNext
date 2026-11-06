import { useEffect, useMemo, useRef, useState } from 'preact/hooks'
import type { FramePlane } from '../Api/Frames'
import { createFrameRenderer } from '../Rendering/FrameRenderer'
import type { FrameRenderer } from '../Rendering/FrameRenderer'
import type { DisplayLayer } from '../Api/Project.generated'
import type { SharedColorScheme } from '../Api/ColorSchemes'
import { findScheme, schemeChoices, schemeStops } from '../Rendering/ColorSchemes'
import { IconView } from './IconView'
import { SceneLayer } from './SceneLayer'
import type { ScenePreview } from '../Wasm/Scene'

interface PictureFrameProps {
  layer: 'Field' | 'Intensity'
  frame?: FramePlane
  index?: number
  stale?: boolean
  error?: string | null
  settings?: DisplayLayer
  scene?: ScenePreview | null
  canvasWidth?: number
  canvasHeight?: number
  projectSchemes: readonly SharedColorScheme[]
  sharedSchemes: readonly SharedColorScheme[]
  canEdit: boolean
  onSchemeChange: (name: string) => Promise<void>
}

export function PictureFrame({ layer, frame, index, stale, error, settings, scene, canvasWidth, canvasHeight, projectSchemes, sharedSchemes, canEdit, onSchemeChange }: PictureFrameProps) {
  const [brightness, setBrightness] = useState(String(settings?.brightness ?? 30))
  const [gain, setGain] = useState(settings?.brightness ?? 30)
  const [renderError, setRenderError] = useState<string | null>(null)
  const host = useRef<HTMLDivElement>(null)
  const renderer = useRef<FrameRenderer | null>(null)
  const choices = useMemo(() => schemeChoices(projectSchemes, sharedSchemes, layer), [projectSchemes, sharedSchemes, layer])
  const scheme = findScheme([...projectSchemes, ...sharedSchemes], settings?.colorScheme ?? '')
  const stops = useMemo(() => schemeStops(scheme), [scheme])
  const display = useMemo(() => ({ brightness: gain, blackLevel: settings?.blackLevel ?? 0, stops }), [gain, settings?.blackLevel, stops])
  const selectedScheme = scheme?.name ?? settings?.colorScheme ?? ''

  useEffect(() => {
    setBrightness(String(settings?.brightness ?? 30))
    setGain(settings?.brightness ?? 30)
  }, [settings?.brightness])

  useEffect(() => {
    if (!host.current) return
    const next = createFrameRenderer(host.current, setRenderError)
    renderer.current = next
    return () => { next.dispose(); renderer.current = null }
  }, [])
  useEffect(() => { renderer.current?.setDisplay(display) }, [display])
  useEffect(() => { if (frame) renderer.current?.setFrame(frame) }, [frame])

  const ratio = frame ? frame.width / frame.height : canvasWidth && canvasHeight ? canvasWidth / canvasHeight : 16 / 9
  const visibleScene = scene && (!frame || frame.width === scene.width && frame.height === scene.height) ? scene : null
  const message = error ?? renderError
  return (
    <section class="picture-frame" aria-label={`${layer} preview`}>
      <header class="picture-header">
        <h2><IconView name={layer === 'Field' ? 'wave' : 'display'} />{layer}</h2>
        <span class="no-frame">{frame ? `Frame ${index}${stale ? ' · outdated' : ''}` : 'No frame'}</span>
        <div class="picture-controls">
          <label>Brightness <input aria-label={`${layer} brightness`} type="number" min="0.0001" max="100000" step="any" value={brightness} disabled={!frame} onInput={event => {
            setBrightness(event.currentTarget.value)
            const value = event.currentTarget.valueAsNumber
            if (Number.isFinite(value) && value >= 0.0001 && value <= 100000) setGain(value)
          }} /></label>
          <button disabled title="Automatic brightness is not available yet">Auto</button>
          <select aria-label={`${layer} colour scheme`} title={scheme?.description || 'Colour scheme'} value={selectedScheme}
            disabled={!canEdit || !settings} onChange={event => { void onSchemeChange(event.currentTarget.value).catch(() => {}) }}>
            {!choices.some(choice => choice.name === selectedScheme) && <option value={selectedScheme}>{selectedScheme || 'Colour scheme'}{selectedScheme && !scheme ? ' (unavailable)' : ''}</option>}
            {choices.map(choice => <option key={choice.name.toLowerCase()} value={choice.name}>{choice.name}</option>)}
          </select>
        </div>
      </header>
      <div class="picture-viewport"><div class="picture-surface" style={{ width: `min(100cqw, calc(100cqh * ${ratio}))`, aspectRatio: String(ratio) }}
        role={!frame && visibleScene ? 'img' : undefined} aria-label={!frame && visibleScene ? `${layer}: scene preview, no simulation frame available` : undefined}>
        {!frame && visibleScene && <SceneLayer scene={visibleScene} layer="image" />}
        <div ref={host} class="frame-canvas" role="img" aria-label={frame ? `${layer}, frame ${index}${stale ? ', outdated simulation' : ''}` : `${layer}: no simulation frame available`} hidden={!frame} />
        {visibleScene && settings?.overlay && settings.overlayGain > 0 && <SceneLayer scene={visibleScene} layer="edges" gain={settings.overlayGain} />}
        {((!frame && !visibleScene) || message) && <div class="picture-empty" role={message ? 'status' : undefined}><IconView name="wave" /><span>{message ?? 'No simulation frame'}</span></div>}
      </div></div>
    </section>
  )
}
