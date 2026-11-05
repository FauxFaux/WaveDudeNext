import { useState } from 'preact/hooks'
import { IconView } from './IconView'

export function PictureFrame({ layer }: { layer: 'Field' | 'Intensity' }) {
  const [brightness, setBrightness] = useState('30')
  return (
    <section class="picture-frame" aria-label={`${layer} preview`}>
      <header class="picture-header">
        <h2><IconView name={layer === 'Field' ? 'wave' : 'display'} />{layer}</h2><span class="no-frame">No frame</span>
        <div class="picture-controls">
          <label>Brightness <input aria-label={`${layer} brightness`} type="number" min="0" max="100" value={brightness} onInput={event => setBrightness(event.currentTarget.value)} /></label>
          <button disabled title="Requires a rendered frame">Auto</button>
        </div>
      </header>
      <div class="picture-viewport"><div class="picture-surface">
        <canvas width="1920" height="1080" aria-label={`${layer}: no simulation frame available`} />
        <div class="picture-empty" aria-hidden="true"><IconView name="wave" /><span>No simulation frame</span></div>
      </div></div>
    </section>
  )
}
