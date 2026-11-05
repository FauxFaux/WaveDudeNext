import type { Probe } from '../Api/Project.generated'
import type { ProbeGraphData } from '../Api/Probes'

export interface GraphRange {
  xMin: number
  xMax: number
  yMin: number
  yMax: number
  logarithmic: boolean
}

export interface GraphTick { position: number, label: string, zero: boolean }
export interface GraphPoint { x: number, y: number, start: boolean }
export interface GraphLayout {
  x: number
  y: number
  width: number
  height: number
  xTicks: GraphTick[]
  yTicks: GraphTick[]
  points: GraphPoint[]
}

// The ranges, ticks and fixed text size follow WaveDudeNext/Probes/ProbeGraph.cs.
const finiteSample = (value: number | null | undefined): value is number => typeof value === 'number' && Number.isFinite(value)

export function graphRange(probe: Probe, data: ProbeGraphData, totalSteps: number): GraphRange {
  const line = probe.kind === 'Line'
  const start = Math.max(0, probe.startAfter)
  const count = Math.min(data.count, data.values?.length ?? 0)
  let xMin = line ? 0 : start
  let xMax = line ? data.profile && data.profileLength > 0 ? data.profileLength : 1 : Math.max(start + 1, probe.autoScaleX ? count : totalSteps)
  if (!line && probe.scaleMode === 'Running') {
    const window = Math.max(10, probe.window)
    xMax = Math.max(count, start + (probe.autoScaleX ? 1 : window))
    xMin = Math.max(start, xMax - window)
  }
  let yMin = probe.yMin
  let yMax = probe.yMax
  if (!Number.isFinite(yMin) || !Number.isFinite(yMax) || !(yMax > yMin)) { yMin = -1; yMax = 1 }
  const values = line ? data.profile : data.values
  const first = line ? 0 : Math.max(0, Math.floor(xMin) - 1)
  const last = line ? (values?.length ?? 0) - 1 : Math.min(count - 1, Math.ceil(xMax))
  let lo = Infinity
  let hi = -Infinity
  if (probe.autoScaleY && values) {
    for (let i = first; i <= last; i++) {
      const value = values[i]
      if (finiteSample(value)) { lo = Math.min(lo, value); hi = Math.max(hi, value) }
    }
    const magnitude = Math.max(Math.abs(lo), Math.abs(hi))
    if (lo <= hi && magnitude > 0) {
      yMax = magnitude * 1.08
      yMin = probe.measurement === 'Amplitude' || lo < 0 ? -yMax : 0
    }
  }
  const logarithmic = probe.layer === 'Intensity' && probe.yScale === 'Logarithmic'
  if (logarithmic) {
    const decades = Math.max(1, Math.min(12, probe.decades))
    if (probe.autoScaleY && hi > 0) {
      yMax = 10 ** (Math.log10(hi) + .08 * decades)
      yMin = yMax / 10 ** decades
    } else {
      yMax = probe.yMax > 0 ? probe.yMax : 1
      yMin = !probe.autoScaleY && probe.yMin > 0 && probe.yMin < yMax ? probe.yMin : yMax / 10 ** decades
    }
  }
  return { xMin, xMax, yMin, yMax, logarithmic }
}

export function niceTicks(lo: number, hi: number, target: number): number[] {
  const span = hi - lo
  if (!(span > 0) || !Number.isFinite(span)) return []
  const raw = span / Math.max(1, target)
  const magnitude = 10 ** Math.floor(Math.log10(raw))
  const mantissa = raw / magnitude
  const step = (mantissa < 1.5 ? 1 : mantissa < 3 ? 2 : mantissa < 7 ? 5 : 10) * magnitude
  if (!(step > 0)) return []
  const ticks: number[] = []
  for (let tick = Math.ceil(lo / step) * step; tick <= hi + step * 1e-9 && ticks.length < 40; tick += step) {
    ticks.push(Math.abs(tick) < step * 1e-9 ? 0 : tick)
  }
  return ticks
}

function logTicks(lo: number, hi: number, target: number): number[] {
  const first = Math.ceil(Math.log10(lo) - 1e-9)
  const last = Math.floor(Math.log10(hi) + 1e-9)
  if (last - first < 1) return niceTicks(lo, hi, target).filter(value => value > 0)
  const every = [1, 2, 3, 5, 10].find(step => (last - first) / step + 1 <= target + 1) ?? 10
  const ticks: number[] = []
  for (let exponent = first; exponent <= last; exponent++) if (exponent % every === 0) ticks.push(10 ** exponent)
  return ticks
}

const numberFormat = new Intl.NumberFormat(undefined, { maximumSignificantDigits: 3, useGrouping: false })
const positionFormat = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1, useGrouping: false })
export const formatValue = (value: number) => Math.abs(value) > 0 && (Math.abs(value) < .001 || Math.abs(value) >= 10000)
  ? value.toExponential(2).replace(/\.?(0+)(?=e)/, '') : numberFormat.format(value)

/** Gate line profiles before StartAfter; point histories retain their latest-value readout. */
export function shownProbeData(probe: Probe, data: ProbeGraphData): ProbeGraphData {
  return probe.kind === 'Line' && data.stepsSampled < probe.startAfter
    ? { count: 0, profileLength: 0, stepsSampled: 0 } : data
}

export function graphReadout(probe: Probe, data: ProbeGraphData): string {
  if (probe.kind === 'Line') {
    let maximum = -Infinity
    for (let i = 0; i < (data.profile?.length ?? 0); i++) {
      const value = data.profile![i]
      if (finiteSample(value)) maximum = Math.max(maximum, value)
    }
    return Number.isFinite(maximum) ? `max ${formatValue(maximum)}` : ''
  }
  const value = data.count > 0 ? data.values?.[Math.min(data.count, data.values?.length ?? 0) - 1] : undefined
  return finiteSample(value) ? `= ${formatValue(value)}` : ''
}

export function layoutGraph(probe: Probe, data: ProbeGraphData, totalSteps: number, width: number, height: number, measure: (text: string) => number): GraphLayout {
  const range = graphRange(probe, data, totalSteps)
  const { xMin, xMax, yMin, yMax, logarithmic } = range
  let yValues: number[] = []
  for (const target of [4, 3, 2]) {
    yValues = logarithmic ? logTicks(yMin, yMax, target) : niceTicks(yMin, yMax, target)
    if ((yValues.length - 1) * 16 <= height - 28) break
  }
  const x = Math.ceil(Math.max(0, ...yValues.map(value => measure(formatValue(value)))) + 8)
  const y = 9
  const plotWidth = Math.max(1, width - x - 10)
  const plotHeight = Math.max(1, height - y - 21)
  const toX = (value: number) => x + (value - xMin) / (xMax - xMin) * plotWidth
  const toY = (value: number) => {
    const low = logarithmic ? Math.log10(yMin) : yMin
    const high = logarithmic ? Math.log10(yMax) : yMax
    const at = logarithmic ? value > 0 ? Math.max(low, Math.log10(value)) : low : value
    return Math.max(-30000, Math.min(30000, y + plotHeight - (at - low) / (high - low) * plotHeight))
  }
  let xTicks: GraphTick[] = []
  for (const target of [5, 3, 2]) {
    xTicks = niceTicks(xMin, xMax, target).map(value => ({
      position: toX(value), label: positionFormat.format(xMax - xMin >= 10 ? Math.round(value) : value), zero: value === 0,
    }))
    if (xTicks.reduce((sum, tick) => sum + measure(tick.label) + 8, -8) <= plotWidth) break
  }
  const line = probe.kind === 'Line'
  const values = line ? data.profile : data.values
  const count = line ? values?.length ?? 0 : Math.min(data.count, values?.length ?? 0)
  const first = line ? 0 : Math.max(0, Math.floor(xMin) - 1)
  const last = line ? count - 1 : Math.min(count - 1, Math.ceil(xMax))
  const points: GraphPoint[] = []
  let start = true
  const atX = (index: number) => line ? x + index / Math.max(1, count - 1) * plotWidth : toX(index + 1)
  const add = (index: number) => {
    const value = values?.[index]
    if (finiteSample(value)) { points.push({ x: atX(index), y: toY(value), start }); start = false }
    else start = true
  }
  // Keep both extrema in their original order in each column, so narrow spikes survive long histories.
  if (values && last - first + 1 > plotWidth * 2) {
    let column = -Infinity
    let minIndex = -1
    let maxIndex = -1
    let gap = false
    const flush = () => {
      if (minIndex < 0) return
      if (gap) start = true
      add(Math.min(minIndex, maxIndex))
      if (minIndex !== maxIndex) add(Math.max(minIndex, maxIndex))
      if (gap) start = true
    }
    for (let i = first; i <= last; i++) {
      const nextColumn = Math.floor(atX(i))
      if (nextColumn !== column) { flush(); column = nextColumn; minIndex = maxIndex = -1; gap = false }
      const value = values[i]
      if (!finiteSample(value)) { gap = true; start = true; continue }
      if (minIndex < 0) minIndex = maxIndex = i
      else {
        if (value < values[minIndex]!) minIndex = i
        if (value > values[maxIndex]!) maxIndex = i
      }
    }
    flush()
  } else for (let i = first; i <= last; i++) add(i)
  return {
    x, y, width: plotWidth, height: plotHeight, xTicks,
    yTicks: yValues.map(value => ({ position: toY(value), label: formatValue(value), zero: value === 0 })), points,
  }
}
