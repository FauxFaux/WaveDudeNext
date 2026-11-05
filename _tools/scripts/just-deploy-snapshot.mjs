import { chmod, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { run } from './process.mjs'

let temporary
try {
  const revision = (await run('git', ['rev-parse', '--short', 'HEAD'], { capture: true })).trim()
  if (!/^[0-9a-f]+$/.test(revision)) throw new Error('Git returned an invalid revision')
  const snapshot = `snapshot-${revision}`
  const destination = 'sek:/srv/wavedude.goeswhere.com'

  // npm is a .cmd script on Windows and needs the native command interpreter.
  // Every command argument here is fixed or derived from the validated Git hash.
  if (process.platform === 'win32') {
    await run(process.env.ComSpec ?? 'cmd.exe', [
      '/d', '/s', '/c', `npm --prefix web run build -- --base /${snapshot}/`,
    ])
  } else {
    await run('npm', ['--prefix', 'web', 'run', 'build', '--', '--base', `/${snapshot}/`])
  }
  await run('rsync', ['-av', '--delete', 'web/dist/', `${destination}/${snapshot}/`])

  temporary = await mkdtemp(join(tmpdir(), 'wavedude-snapshot-'))
  const redirect = join(temporary, 'index.html')
  await writeFile(redirect,
    `<!doctype html>\n<html lang="en"><meta charset="utf-8"><title>WaveDude</title><meta http-equiv="refresh" content="0;url=/${snapshot}/"><a href="/${snapshot}/">Open latest snapshot</a></html>\n`,
  )
  await chmod(redirect, 0o644)
  // A relative source also avoids Windows drive letters being parsed as rsync hosts.
  await run('rsync', ['-av', 'index.html', `${destination}/index.html`], { cwd: temporary })
} catch (error) {
  console.error(error.message)
  process.exitCode = error.exitCode ?? 1
} finally {
  if (temporary) await rm(temporary, { recursive: true, force: true })
}
