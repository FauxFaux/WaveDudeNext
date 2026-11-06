import assert from 'node:assert/strict'
import { writeFrames } from '../src/Wasm/FrameStore.ts'

function deferred() {
  let resolve, reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

function controlledDirectory(count) {
  const gates = Array.from({ length: count }, deferred)
  const started = Array.from({ length: count }, deferred)
  const closed = Array.from({ length: count }, deferred)
  const files = new Map(), aborted = []
  let active = 0, maximum = 0
  return { gates, started, closed, files, aborted, get active() { return active }, get maximum() { return maximum },
    directory: { async getFileHandle(name) {
      const index = Number(name.split('.')[0])
      return { async createWritable() {
        active++
        maximum = Math.max(maximum, active)
        let bytes
        return {
          async write(blob) { bytes = new Uint8Array(await blob.arrayBuffer()) },
          async close() {
            started[index].resolve()
            await gates[index].promise
            files.set(index, bytes)
            active--
            closed[index].resolve()
          },
          async abort() { aborted.push(index); active-- },
        }
      } }
    } },
  }
}

const frame = index => ({ bytes: Uint8Array.of(index, 255), probeBytes: Uint8Array.of(64 + index) })

// Out-of-order storage must leave notifications ordered, stop production at two
// outstanding frames and drain both final writes before completion.
{
  const storage = controlledDirectory(4)
  const produced = [], stored = []
  const notifications = Array.from({ length: 4 }, deferred)
  let completed = false
  const run = writeFrames(storage.directory, 4, async index => { produced.push(index); return frame(index) }, (index, value) => {
    assert.deepEqual(storage.files.get(index), Uint8Array.of(index, 255, 64 + index), 'Only complete, closed files are announced')
    assert.deepEqual(value, frame(index), 'Later production preserves earlier owned buffers')
    stored.push(index)
    notifications[index].resolve()
  }).then(() => { completed = true })
  await storage.started[1].promise
  assert.deepEqual(produced, [0, 1], 'The second frame is produced before the first write closes')
  storage.gates[1].resolve()
  await storage.closed[1].promise
  assert.deepEqual(stored, [], 'The second close cannot overtake the first notification')
  assert.equal(completed, false)
  storage.gates[0].resolve()
  await notifications[1].promise
  await storage.started[3].promise
  assert.deepEqual(stored, [0, 1])
  storage.gates[3].resolve()
  await storage.closed[3].promise
  assert.equal(completed, false, 'Completion waits for the penultimate write too')
  storage.gates[2].resolve()
  await run
  assert.deepEqual(stored, [0, 1, 2, 3])
  assert.equal(storage.maximum, 2)
  assert.equal(storage.active, 0)
}

// Publish the first frame while the producer is still advancing towards the next
// capture. A long capture interval must not hide already stored progress.
{
  const storage = controlledDirectory(1)
  const advancing = deferred(), stop = deferred(), notification = deferred()
  const error = new Error('Producer failed')
  const run = writeFrames(storage.directory, 2, async index => {
    if (index === 0) return frame(index)
    advancing.resolve()
    await stop.promise
    throw error
  }, index => { assert.equal(index, 0); notification.resolve() })
  const rejection = assert.rejects(run, value => value === error)
  await advancing.promise
  await storage.started[0].promise
  storage.gates[0].resolve()
  await notification.promise
  stop.resolve()
  await rejection
  assert.equal(storage.active, 0)
}

// A later write can fail while the queue waits for an earlier file. Handle its
// rejection immediately, stop production, settle the earlier write and then fail.
{
  const storage = controlledDirectory(3)
  const error = new Error('Disc full')
  const produced = [], stored = []
  let settled = false
  const run = writeFrames(storage.directory, 3, async index => { produced.push(index); return frame(index) }, index => stored.push(index))
  const rejection = assert.rejects(run, value => { settled = true; return value === error })
  await storage.started[1].promise
  storage.gates[1].reject(error)
  // Allow the asynchronous write failure/abort handlers to run before checking.
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(settled, false, 'Outstanding earlier writes are settled before failure returns')
  storage.gates[0].resolve()
  await rejection
  assert.deepEqual(produced, [0, 1])
  assert.deepEqual(stored, [])
  assert.deepEqual(storage.aborted, [1])
  assert.equal(storage.active, 0)
}

// Producer failures must also settle outstanding writes before returning.
{
  const storage = controlledDirectory(1)
  const error = new Error('Solver failed')
  let settled = false
  const run = writeFrames(storage.directory, 2, async index => {
    if (index === 1) throw error
    return frame(index)
  }, () => {})
  const rejection = assert.rejects(run, value => { settled = true; return value === error })
  await storage.started[0].promise
  assert.equal(settled, false)
  storage.gates[0].resolve()
  await rejection
  assert.equal(storage.active, 0)
}

console.log('Bounded frame writes passed overlap, ordered publication, buffer ownership, drain and failure cleanup checks.')
