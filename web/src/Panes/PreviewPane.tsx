import { IconView } from '../Controls/IconView'
import { PictureFrame } from '../Controls/PictureFrame'
import { TimelineBar } from '../Controls/TimelineBar'

interface PreviewPaneProps {
  orientation: 'stacked' | 'side-by-side'
  onOrientationChange: (orientation: PreviewPaneProps['orientation']) => void
}

export function PreviewPane({ orientation, onOrientationChange }: PreviewPaneProps) {
  return (
    <section class="preview-pane" aria-label="Preview">
      <div class={`pictures ${orientation}`}><PictureFrame layer="Field" /><PictureFrame layer="Intensity" /></div>
      <TimelineBar />
      <div class="transport-bar" aria-label="Preview controls">
        <button id="PreviewRun" class="run-button" disabled title="Requires the simulation server"><IconView name="play" />Run</button>
        <button id="PreviewPauseRun" class="icon-button" disabled title="Pause simulation" aria-label="Pause simulation"><IconView name="pause" /></button>
        <button class="icon-button" disabled title="Stop simulation" aria-label="Stop simulation"><IconView name="stop" /></button>
        <span class="toolbar-divider" />
        <div class="playback-buttons">
          {([['first', 'Go to start'], ['previous', 'Previous frame'], ['play', 'Play preview'], ['next', 'Next frame'], ['last', 'Go to end'], ['loop', 'Loop preview']] as const).map(([icon, label]) => (
            <button key={label} class="icon-button" disabled title={label} aria-label={label}><IconView name={icon} /></button>
          ))}
        </div>
        <span class="frame-readout">Frame <b>— / —</b><span>Step —</span></span>
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
