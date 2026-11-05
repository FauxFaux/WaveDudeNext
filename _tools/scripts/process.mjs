import { spawn } from 'node:child_process'
import { constants } from 'node:os'
import { fileURLToPath } from 'node:url'

export const repository = fileURLToPath(new URL('../../', import.meta.url))

// Run from the checkout regardless of the caller's directory, without a shell.
export async function run(command, args, { capture = false, cwd = repository } = {}) {
  const child = spawn(command, args, {
    cwd,
    stdio: ['inherit', capture ? 'pipe' : 'inherit', 'inherit'],
  })
  let output = ''
  if (capture) {
    child.stdout.setEncoding('utf8')
    child.stdout.on('data', chunk => { output += chunk })
  }

  // Let callers finish their cleanup when a build is interrupted.
  let interrupted
  const interrupt = signal => {
    interrupted = signal
    child.kill(signal)
  }
  process.on('SIGINT', interrupt)
  process.on('SIGTERM', interrupt)
  try {
    await new Promise((resolve, reject) => {
      child.once('error', reject)
      child.once('close', (code, signal) => {
        const termination = interrupted ?? signal
        if (code === 0 && !termination) {
          resolve()
        } else {
          const error = new Error(`${command} ${termination ? `terminated by ${termination}` : `exited with code ${code}`}`)
          error.exitCode = termination ? 128 + constants.signals[termination] : code ?? 1
          reject(error)
        }
      })
    })
    return output
  } finally {
    process.off('SIGINT', interrupt)
    process.off('SIGTERM', interrupt)
  }
}
