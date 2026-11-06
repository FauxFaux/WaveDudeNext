import { runComputeWorker } from '../../../rust-kernel/parallel.mjs'

self.onmessage = async ({ data }) => {
  await runComputeWorker(data, message => self.postMessage(message))
  self.close()
}
