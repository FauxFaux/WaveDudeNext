import kernelUrl from '../assets/rust-kernel.wasm?url'
import type { RustKernelMessage } from './RustKernel.worker'

/** Exercise two independent Wasm instances concurrently; no memory is shared yet. */
export async function checkRustKernel(): Promise<number[]> {
  const workers: Worker[] = []
  const timers: ReturnType<typeof setTimeout>[] = []
  try {
    return await Promise.all(Array.from({ length: 2 }, () => new Promise<number>((resolve, reject) => {
      const worker = new Worker(new URL('./RustKernel.worker.ts', import.meta.url), { type: 'module' })
      workers.push(worker)
      timers.push(setTimeout(() => reject(new Error('Rust kernel worker timed out')), 15_000))
      worker.onmessage = ({ data }: MessageEvent<RustKernelMessage>) => {
        if (data.type === 'error') reject(new Error(data.error))
        else if (data.version !== 1) reject(new Error(`Unexpected Rust kernel ABI: ${data.version}`))
        else resolve(data.version)
      }
      worker.onerror = event => reject(new Error(event.message || 'Rust kernel worker failed'))
      worker.onmessageerror = () => reject(new Error('Rust kernel worker response could not be read'))
      worker.postMessage({ url: new URL(kernelUrl, window.location.href).href })
    })))
  } finally {
    timers.forEach(clearTimeout)
    workers.forEach(worker => worker.terminate())
  }
}
