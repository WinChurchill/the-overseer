/**
 * Concurrent current.json reader used by test/spool.test.js.
 *
 * Runs in a worker thread so the reader and the writer really overlap: a
 * non-atomic replacement (truncate-then-write) would let a reader observe a
 * partial document. Sanitized, synthetic input only.
 */
const { parentPort, workerData } = require('node:worker_threads')
const fs = require('node:fs')

let reads = 0
let torn = 0
let missing = 0
const deadline = Date.now() + workerData.ms
while (Date.now() < deadline) {
  let text
  try {
    text = fs.readFileSync(workerData.path, 'utf8')
  } catch (error) {
    if (error && error.code === 'ENOENT') {
      missing += 1
      continue
    }
    torn += 1
    continue
  }
  reads += 1
  try {
    const parsed = JSON.parse(text)
    if (typeof parsed.marker !== 'number') torn += 1
  } catch {
    torn += 1
  }
}
parentPort.postMessage({ reads, torn, missing })
