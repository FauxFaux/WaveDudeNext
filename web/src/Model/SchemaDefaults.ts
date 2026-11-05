import { z } from 'zod'

/** Build editor values from the defaults and ranges in the model schema snapshot. */
export function initialValue(schema: z.ZodType, key = ''): unknown {
  const preset = schema.meta()?.default
  if (preset !== undefined) return structuredClone(preset)
  if (schema instanceof z.ZodNullable) return null
  if (schema instanceof z.ZodObject) return Object.fromEntries(Object.entries(schema.shape).map(([key, child]) => [key, initialValue(child as z.ZodType, key)]))
  if (schema instanceof z.ZodArray) return []
  if (schema instanceof z.ZodLiteral) return schema.value
  if (schema instanceof z.ZodNumber) return Math.max(0, schema.minValue ?? 0)
  if (schema instanceof z.ZodBoolean) return false
  if (schema instanceof z.ZodEnum) return schema.options[0]
  if (schema instanceof z.ZodUUID) return crypto.randomUUID()
  return key === 'colorText' ? '#ffffff' : ''
}
