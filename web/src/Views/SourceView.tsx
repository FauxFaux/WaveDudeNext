import type { PulseShape, SourceMode } from '../Model/Source'
import { isSourceNumberValid, sourceNumberFields } from './SourceDraft'
import type { SourceDraft } from './SourceDraft'

interface SourceViewProps {
  source: SourceDraft
  onChange: (source: SourceDraft) => void
}

export function SourceView({ source, onChange }: SourceViewProps) {
  function number(key: keyof typeof sourceNumberFields, label: string, disabled = false) {
    const { min, max, step } = sourceNumberFields[key]
    return (
      <label class="form-row"><span>{label}</span>
        <input type="number" min={min} max={max} step={step} value={source[key]} disabled={disabled}
          aria-invalid={!isSourceNumberValid(key, source[key])}
          onInput={event => onChange({ ...source, [key]: event.currentTarget.value })} />
      </label>
    )
  }
  return (
    <div class="property-form">
      <details open class="form-section"><summary>General</summary><div class="form-fields">
        <label class="form-row"><span>Name</span><input value={source.name} onInput={event => onChange({ ...source, name: event.currentTarget.value })} /></label>
        <label class="checkbox-row"><input type="checkbox" checked={source.enabled} onChange={event => onChange({ ...source, enabled: event.currentTarget.checked })} />Enabled</label>
      </div></details>
      <details open class="form-section"><summary>Position</summary><div class="form-fields">
        {number('x', 'X')}{number('y', 'Y')}
      </div></details>
      <details open class="form-section"><summary>Wave</summary><div class="form-fields">
        {number('frequency', 'Frequency')}{number('amplitude', 'Amplitude')}{number('phase', 'Phase')}
      </div></details>
      <details open class="form-section"><summary>Timing</summary><div class="form-fields">
        <label class="form-row"><span>Mode</span><select value={source.mode} onChange={event => onChange({ ...source, mode: event.currentTarget.value as SourceMode })}><option>Continuous</option><option>Pulsed</option></select></label>
        <label class="form-row"><span>Pulse shape</span><select disabled={source.mode === 'Continuous'} value={source.pulseShape} onChange={event => onChange({ ...source, pulseShape: event.currentTarget.value as PulseShape })}><option value="Gaussian">Gaussian</option><option value="SmoothedSquare">Smoothed square</option></select></label>
        {number('pulseWidth', 'Pulse width', source.mode === 'Continuous')}
        {number('offWidth', 'Off width', source.mode === 'Continuous')}
        {number('detachAfter', 'Detach after step')}
      </div></details>
    </div>
  )
}
