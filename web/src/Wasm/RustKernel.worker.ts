export type RustKernelMessage =
  | { type: 'result', version: number }
  | { type: 'error', error: string }

const worker = globalThis as unknown as {
  addEventListener: (type: 'message', listener: (event: MessageEvent<{ url: string }>) => void) => void
  postMessage: (message: RustKernelMessage) => void
}

worker.addEventListener('message', async ({ data }) => {
  try {
    const response = await fetch(data.url)
    if (!response.ok) throw new Error(`Rust kernel fetch failed: ${response.status}`)
    const { instance } = await WebAssembly.instantiateStreaming(response)
    const version = instance.exports.kernel_abi_version
    if (typeof version !== 'function') throw new Error('Rust kernel ABI export is missing')
    worker.postMessage({ type: 'result', version: version() as number })
  } catch (error) {
    worker.postMessage({ type: 'error', error: error instanceof Error ? error.message : String(error) })
  }
})
