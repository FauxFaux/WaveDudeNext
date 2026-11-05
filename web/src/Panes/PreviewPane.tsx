import { useEffect, useState } from 'preact/hooks'
import { getColorSchemes } from '../Api/ColorSchemes'
import type { SharedColorScheme } from '../Api/ColorSchemes'
import { useFrames } from '../Api/useFrames'
import type { RunStatus } from '../Api/Workspace'
import type { Project, ProjectSelection } from '../Model/Project'
import { IconView } from '../Controls/IconView'
import { PictureFrame } from '../Controls/PictureFrame'
import { ProbeGraphs } from '../Controls/ProbeGraphs'
import { TimelineBar } from '../Controls/TimelineBar'

interface PreviewPaneProps {
  workspaceId?: string
  project?: Project
  canEdit: boolean
  onSchemeChange: (layer: 'field' | 'intensity', name: string) => Promise<void>
  selection?: ProjectSelection
  onSelectProbe: (id: string) => void
  canRun: boolean
  canStop: boolean
  onRun: () => void
  onStop: () => void
  run?: RunStatus | null
  orientation: 'stacked' | 'side-by-side'
  onOrientationChange: (orientation: PreviewPaneProps['orientation']) => void
}

export function PreviewPane({ orientation, onOrientationChange, canRun, canStop, onRun, onStop, run, workspaceId, project, canEdit, onSchemeChange, selection, onSelectProbe }: PreviewPaneProps) {
  const [schemes, setSchemes] = useState<SharedColorScheme[]>([])
  const [schemeError, setSchemeError] = useState<string | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    void getColorSchemes(controller.signal).then(schemes => {
      if (!controller.signal.aborted) setSchemes(schemes)
    }).catch(error => {
      if (!controller.signal.aborted) setSchemeError(error instanceof Error ? error.message : 'Could not load colour schemes.')
    })
    return () => controller.abort()
  }, [])
  const [position, setPosition] = useState<{ runId: string, index: number | null } | null>(null)
  const count = run?.frameCount ?? 0
  const last = Math.max(0, count - 1)
  const selected = position?.runId === run?.runId ? position?.index : null
  const index = selected == null ? last : Math.min(selected, last)
  const seek = (index: number | null) => { if (run) setPosition({ runId: run.runId, index }) }
  const includeProbes = !!project?.probes.some(probe => probe.active) && !run?.probesStale && !run?.isStale
  const { frame, error } = useFrames(workspaceId, run, index, includeProbes)
  // Both pictures and recordings are presented together by useFrames, after identity validation.
  const recordings = !run?.probesStale && !run?.isStale && !frame?.probes?.probesStale ? frame?.probes?.probes : undefined
  return (
    <section class="preview-pane" aria-label="Preview">
      <div class="preview-pictures">
        <div class={`pictures ${orientation}`}>
          <PictureFrame layer="Field" frame={frame?.field} index={frame?.index} stale={run?.isStale} error={error ?? schemeError}
            settings={project?.display.field} projectSchemes={project?.colorSchemes ?? []} sharedSchemes={schemes} canEdit={canEdit}
            onSchemeChange={name => onSchemeChange('field', name)} />
          <PictureFrame layer="Intensity" frame={frame?.intensity} index={frame?.index} stale={run?.isStale} error={error ?? schemeError}
            settings={project?.display.intensity} projectSchemes={project?.colorSchemes ?? []} sharedSchemes={schemes} canEdit={canEdit}
            onSchemeChange={name => onSchemeChange('intensity', name)} />
        </div>
        <ProbeGraphs key={workspaceId} probes={project?.probes ?? []} recordings={recordings}
          totalSteps={run?.totalSteps ?? project?.simulation.steps ?? 1} orientation={orientation} onSelect={onSelectProbe}
          selectedId={typeof selection !== 'string' && selection?.collection === 'probes' ? selection.id : undefined} />
      </div>
      <TimelineBar index={index} frameCount={count} visualizeEvery={run?.visualizeEvery ?? 1} onSeek={seek} />
      <div class="transport-bar" aria-label="Preview controls">
        <button id="PreviewRun" class="run-button" disabled={!canRun} onClick={onRun} title="Run simulation"><IconView name="play" />Run</button>
        <button id="PreviewPauseRun" class="icon-button" disabled title="Pause simulation" aria-label="Pause simulation"><IconView name="pause" /></button>
        <button class="icon-button" disabled={!canStop} onClick={onStop} title="Stop simulation" aria-label="Stop simulation"><IconView name="stop" /></button>
        <span class="toolbar-divider" />
        <div class="playback-buttons">
          <button class="icon-button" disabled={!count || index === 0} onClick={() => seek(0)} title="Go to start" aria-label="Go to start"><IconView name="first" /></button>
          <button class="icon-button" disabled={!count || index === 0} onClick={() => seek(index - 1)} title="Previous frame" aria-label="Previous frame"><IconView name="previous" /></button>
          <button class="icon-button" disabled title="Play preview" aria-label="Play preview"><IconView name="play" /></button>
          <button class="icon-button" disabled={!count || index === last} onClick={() => seek(index + 1)} title="Next frame" aria-label="Next frame"><IconView name="next" /></button>
          <button class="icon-button" disabled={!count} onClick={() => seek(null)} title="Follow newest frame" aria-label="Go to end" aria-pressed={selected == null}><IconView name="last" /></button>
          <button class="icon-button" disabled title="Loop preview" aria-label="Loop preview"><IconView name="loop" /></button>
        </div>
        <span class="frame-readout">Frame <b>{frame?.index ?? '—'}</b><span>Step {frame && run ? frame.index * run.visualizeEvery : '—'}</span><span>Buffered <b>{run ? `${run.frameCount} / ${run.totalFrames}` : '— / —'}</b></span></span>
        <div class="transport-options">
          <label class="speed-control">Speed <select aria-label="Playback speed" disabled><option>1×</option></select></label>
          <span class="toolbar-divider" />
          <button class="icon-button" title="Side-by-side previews" aria-label="Side-by-side previews" aria-pressed={orientation === 'side-by-side'} onClick={() => onOrientationChange('side-by-side')}><IconView name="sideBySide" /></button>
          <button class="icon-button" title="Stacked previews" aria-label="Stacked previews" aria-pressed={orientation === 'stacked'} onClick={() => onOrientationChange('stacked')}><IconView name="stacked" /></button>
        </div>
      </div>
    </section>
  )
}
