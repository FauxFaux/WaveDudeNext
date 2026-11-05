import { cp, mkdtemp, rm, stat } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { repository, run } from './process.mjs'

let output
try {
  output = await mkdtemp(join(tmpdir(), 'wavedude-wasm-'))
  // Docker parses --output as CSV, including when a host path contains commas.
  const destination = `"dest=${output.replaceAll('"', '""')}"`
  await run('docker', [
    'build', '--file', '_tools/build/Dockerfile', '--target', 'wasm',
    '--output', `type=local,${destination}`, '.',
  ])
  for (const file of ['main.mjs', '_framework/dotnet.js']) {
    if (!(await stat(join(output, file))).isFile()) {
      throw new Error(`Missing generated runtime file: ${file}`)
    }
  }

  const bundle = join(repository, 'web/public/wasm')
  await rm(bundle, { recursive: true, force: true })
  await cp(output, bundle, { recursive: true })
} catch (error) {
  console.error(error.message)
  process.exitCode = error.exitCode ?? 1
} finally {
  if (output) await rm(output, { recursive: true, force: true })
}
