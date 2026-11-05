// Local editor state until the server supplies the project model and validation.
export function createSource() {
  return {
    name: 'Source 1', enabled: true, x: '960', y: '540',
    frequency: '100', amplitude: '100', phase: '0',
    mode: 'Continuous', pulseShape: 'Gaussian', pulseWidth: '10', offWidth: '10', detachAfter: '-1',
  }
}

export type SourceDraft = ReturnType<typeof createSource>

interface SourceViewProps {
  source: SourceDraft
  onChange: (source: SourceDraft) => void
}

export function SourceView({ source, onChange }: SourceViewProps) {
  function number(key: keyof SourceDraft, label: string, min: number, max?: number, disabled = false) {
    return (
      <label class="form-row"><span>{label}</span>
        <input type="number" min={min} max={max} step="any" value={String(source[key])} disabled={disabled}
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
        {number('x', 'X', 0)}{number('y', 'Y', 0)}
      </div></details>
      <details open class="form-section"><summary>Wave</summary><div class="form-fields">
        {number('frequency', 'Frequency', 0.01, 255)}{number('amplitude', 'Amplitude', 0, 100)}{number('phase', 'Phase', -180, 180)}
      </div></details>
      <details open class="form-section"><summary>Timing</summary><div class="form-fields">
        <label class="form-row"><span>Mode</span><select value={source.mode} onChange={event => onChange({ ...source, mode: event.currentTarget.value })}><option>Continuous</option><option>Pulsed</option></select></label>
        <label class="form-row"><span>Pulse shape</span><select disabled={source.mode === 'Continuous'} value={source.pulseShape} onChange={event => onChange({ ...source, pulseShape: event.currentTarget.value })}><option value="Gaussian">Gaussian</option><option value="SmoothedSquare">Smoothed square</option></select></label>
        {number('pulseWidth', 'Pulse width', 1, undefined, source.mode === 'Continuous')}
        {number('offWidth', 'Off width', 1, undefined, source.mode === 'Continuous')}
        {number('detachAfter', 'Detach after step', -1)}
      </div></details>
    </div>
  )
}
