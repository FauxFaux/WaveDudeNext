import { z } from 'zod'
import { ColorSchemeSchema } from './Project.generated'

const SharedColorSchemeSchema = ColorSchemeSchema.omit({ id: true })
export type SharedColorScheme = z.infer<typeof SharedColorSchemeSchema>

export async function getColorSchemes(signal: AbortSignal): Promise<SharedColorScheme[]> {
  const response = await fetch('/api/color-schemes', { signal })
  if (!response.ok) throw new Error(`Could not load colour schemes (HTTP ${response.status}).`)
  return z.array(SharedColorSchemeSchema).parse(await response.json())
}
