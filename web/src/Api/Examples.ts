import { z } from 'zod'

const ExampleEntrySchema = z.strictObject({
  id: z.string().min(1),
  number: z.number().int().min(1),
  title: z.string().min(1),
})
export type ExampleEntry = z.infer<typeof ExampleEntrySchema>

export async function getExamples(signal: AbortSignal): Promise<ExampleEntry[]> {
  const response = await fetch('/api/examples', { signal })
  if (!response.ok) throw new Error(`Could not list examples (HTTP ${response.status}).`)
  return z.array(ExampleEntrySchema).parse(await response.json())
}
