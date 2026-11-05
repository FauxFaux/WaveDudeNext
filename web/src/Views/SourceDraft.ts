import type { Source } from '../Model/Source'

import { SourceSchema } from '../Api/Project.generated'

const numberFields = ['x', 'y', 'frequency', 'amplitude', 'phase', 'pulseWidth', 'offWidth', 'detachAfter'] as const
export const sourceNumberFields = Object.fromEntries(numberFields.map(key => {
  const schema = SourceSchema.shape[key]
  return [key, { min: schema.minValue, max: schema.maxValue, step: schema.isInt ? 1 : 'any' }]
})) as Record<typeof numberFields[number], { min: number | null, max: number | null, step: number | 'any' }>

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
  return text.trim() !== '' && SourceSchema.shape[key].safeParse(Number(text)).success
}

/** Keep incomplete edits in the draft; only valid numbers enter the project model. */
export function applySourceDraft(source: Source, draft: SourceDraft): Source {
  const next = { ...source, name: draft.name, enabled: draft.enabled, mode: draft.mode, pulseShape: draft.pulseShape }
  for (const key of Object.keys(sourceNumberFields) as NumberField[]) {
    if (isSourceNumberValid(key, draft[key])) next[key] = Number(draft[key])
  }
  return next
}
