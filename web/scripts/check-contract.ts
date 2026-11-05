import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createMutationQueue } from '../src/Api/MutationQueue.ts'
import { ProjectSchema, SourceSchema, SourceArraySchema } from '../src/Api/Project.generated.ts'

const root = fileURLToPath(new URL('../../', import.meta.url))
const directory = mkdtempSync(join(tmpdir(), 'wavedude-contract-'))
try {
  const fixtures = join(directory, 'projects.json')
  execFileSync('dotnet', [join(root, '_tools/contract_gen/bin/Release/net10.0/contract_gen.dll'), '--fixtures', fixtures], { cwd: root, stdio: 'pipe' })
  const projects: unknown[] = JSON.parse(readFileSync(fixtures, 'utf8'))
  assert.equal(projects.length, 26)
  const parsed = projects.map(project => ProjectSchema.parse(project))
  assert.equal(new Set(parsed.flatMap(project => project.parts.map(part => part.kind))).size, 13)
  assert.ok(parsed.some(project => project.sources.some(source => source.array === null)))
  assert.ok(parsed.some(project => project.sources.some(source => source.array !== null)))
  assert.ok(parsed.some(project => project.scene.imageAssetId !== null))
  assert.equal(SourceSchema.shape.frequency.meta()?.default, 100)
  assert.ok(SourceSchema.shape.frequency.safeParse(0.01).success)
  assert.ok(SourceSchema.shape.frequency.safeParse(255).success)
  for (const value of [0, 256, NaN, Infinity, '100', null, undefined]) {
    assert.equal(SourceSchema.shape.frequency.safeParse(value).success, false)
  }
  assert.ok(SourceArraySchema.shape.angle.safeParse(180).success)
  assert.equal(SourceArraySchema.shape.angle.safeParse(-180).success, false)
  assert.equal(SourceSchema.shape.pulseWidth.safeParse(1.5).success, false)
  assert.equal(ProjectSchema.safeParse({ ...parsed[0], elements: [] }).success, false)
  const missing = { ...parsed[0].sources[0] } as Record<string, unknown>
  delete missing.frequency
  assert.equal(SourceSchema.safeParse(missing).success, false)
  console.log(`Validated ${parsed.length} projects (24 examples, all 13 part kinds, image references), nullability, required fields and range boundaries.`)
} finally {
  rmSync(directory, { recursive: true, force: true })
}

// A slow acknowledgement cannot send the next edit with an old revision.
const queue = createMutationQueue({ revision: 1, name: 'Initial', frequency: 100 })
let acknowledge!: (value: { revision: number, name: string, frequency: number }) => void
let calls = 0
const first = queue.enqueue(async current => {
  calls++
  assert.equal(current.revision, 1)
  return new Promise(resolve => { acknowledge = resolve })
})
const second = queue.enqueue(async current => {
  calls++
  assert.deepEqual(current, { revision: 2, name: 'Renamed', frequency: 100 })
  return { ...current, revision: 3, frequency: 125 }
})
await Promise.resolve()
assert.equal(calls, 1)
acknowledge({ revision: 2, name: 'Renamed', frequency: 100 })
await first
assert.deepEqual(await second, { revision: 3, name: 'Renamed', frequency: 125 })
const failing = queue.enqueue(async () => { throw new Error('Network failure') })
const blocked = queue.enqueue(async current => { calls++; return current })
await assert.rejects(failing, /Network failure/)
await assert.rejects(blocked, /edits are paused/)
assert.equal(calls, 2)
console.log('Validated serialised mutations, acknowledgement ordering and failure handling.')
