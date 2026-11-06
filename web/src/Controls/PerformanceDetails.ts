import type { RunPerformance } from '../Api/Frames'

/** Explain elapsed phase/band timings without adding overlapping totals together. */
export function performanceDetails(timing: RunPerformance): string {
  const seconds = (value: number) => `${(value / 1000).toFixed(3)} s`
  const mean = (value: number, count: number) => `${(count ? value / count : 0).toFixed(3)} ms`
  const phases = timing.frameTimings
  const schedule = timing.scheduling
  const lines = [
    `Averages over ${(timing.elapsedMilliseconds / 1000).toFixed(2)} s since stepping started, including frame capture and disc writes.`,
    'Parallelism = summed elapsed solver work across all threads ÷ elapsed run time.',
    `Coordinator: ${seconds(timing.coordinatorMilliseconds)} work`,
    `Capture + buffer copy: ${seconds(phases.captureMilliseconds)} (${mean(phases.captureMilliseconds, phases.frames)}/frame)`,
    `Observation + copy: ${seconds(phases.observeMilliseconds)} · Probe packing: ${seconds(phases.packMilliseconds)}`,
    `Advance batches, including barriers: ${seconds(phases.advanceMilliseconds)}`,
    `Disc writes: ${seconds(timing.storageTimings.writeMilliseconds)} overlapping elapsed time · Writer back-pressure: ${seconds(timing.storageTimings.capacityWaitMilliseconds)}`,
  ]
  if (schedule) lines.push(`Substep barriers: ${schedule.barriers} · Elapsed: ${seconds(schedule.barrierMilliseconds)} · After last kernel finish: ${seconds(schedule.resumeMilliseconds)} (${mean(schedule.resumeMilliseconds, schedule.barriers)}/barrier)`)
  timing.workerMilliseconds.forEach((work, index) => {
    const idle = timing.elapsedMilliseconds > 0 ? 100 * Math.max(0, 1 - work / timing.elapsedMilliseconds) : 0
    const stats = schedule?.workers[index]
    const rows = stats ? stats.stripeCount > 1
      ? `, ${stats.rowCount} rows in ${stats.stripeCount} stripes of up to ${stats.stripeRows}`
      : `, rows ${stats.rowStart}–${stats.rowEnd - 1}` : ''
    lines.push(`Worker ${index + 1}${rows}: ${seconds(work)} work · ${idle.toFixed(0)}% idle (est.)`)
    if (stats) {
      const last = stats.calls ? stats.lastFinishes * 100 / stats.calls : 0
      lines.push(`  Work per substep: mean ${mean(work, stats.calls)}, max ${stats.maxCallMilliseconds.toFixed(3)} ms · Last kernel finish: ${last.toFixed(0)}%`)
      lines.push(`  Launch delay: mean ${mean(stats.dispatchMilliseconds, stats.calls)}, max ${stats.maxDispatchMilliseconds.toFixed(3)} ms · Early-finish wait: ${seconds(stats.earlyFinishMilliseconds)} · Between dispatches: ${seconds(stats.waitForWorkMilliseconds)}`)
    }
  })
  lines.push('Launch = dispatch to kernel entry. Early-finish wait ends when the last band finishes; between-dispatch waits include it and capture/storage gaps.')
  lines.push('Elapsed timings overlap and are not CPU time. Browser scheduling/profiling can affect them; timestamps use a common time origin.')
  return lines.join('\n')
}
