/** Serialise edits against the last acknowledgement; pause after an ambiguous failure. */
export function createMutationQueue<T>(initial: T) {
  let current = initial
  let tail = Promise.resolve()
  let failed = false
  return {
    enqueue(operation: (acknowledged: T) => Promise<T>): Promise<T> {
      const task = tail.then(async () => {
        if (failed) throw new Error('Project edits are paused. Your draft is still available; reload to reconnect.')
        current = await operation(current)
        return current
      })
      tail = task.then(() => {}, () => { failed = true })
      return task
    },
  }
}
