import { z } from 'zod'
import { ColorSchemeSchema } from './Project.generated'

const SharedColorSchemeSchema = ColorSchemeSchema.omit({ id: true })
export type SharedColorScheme = z.infer<typeof SharedColorSchemeSchema>
