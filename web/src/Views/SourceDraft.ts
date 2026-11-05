import type { Source } from '../Model/Source'

export const sourceNumberFields = {
  x: { min: 0, max: 2147483647, step: 1 },
  y: { min: 0, max: 2147483647, step: 1 },
  frequency: { min: 0.01, max: 255, step: 'any' },
  amplitude: { min: 0, max: 100, step: 'any' },
  phase: { min: -180, max: 180, step: 'any' },
  pulseWidth: { min: 1, max: 2147483647, step: 1 },
  offWidth: { min: 1, max: 2147483647, step: 1 },
  detachAfter: { min: -1, max: 2147483647, step: 1 },
} as const

type NumberField = keyof typeof sourceNumberFields
export type SourceDraft = Omit<Source, NumberField> & Record<NumberField, string>

export function createSourceDraft(source: Source): SourceDraft {
  return {
    ...source, x: String(source.x), y: String(source.y),
    frequency: String(source.frequency), amplitude: String(source.amplitude), phase: String(source.phase),
    pulseWidth: String(source.pulseWidth), offWidth: String(source.offWidth), detachAfter: String(source.detachAfter),
  }
}

export function isSourceNumberValid(key: NumberField, text: string): boolean {
  const value = Number(text)
  const { min, max, step } = sourceNumberFields[key]
  return text.trim() !== '' && Number.isFinite(value) && value >= min && value <= max
    && (step === 'any' || Number.isInteger(value))
}

/** Keep incomplete edits in the draft; only valid numbers enter the project model. */
export function applySourceDraft(source: Source, draft: SourceDraft): Source {
  const next = { ...source, name: draft.name, enabled: draft.enabled, mode: draft.mode, pulseShape: draft.pulseShape }
  for (const key of Object.keys(sourceNumberFields) as NumberField[]) {
    if (isSourceNumberValid(key, draft[key])) next[key] = Number(draft[key])
  }
  return next
}
